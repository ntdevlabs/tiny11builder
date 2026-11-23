using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private const string NanoStage = "Nano11 reductions";

    /// <summary>What a Nano11 reduction operates on: the session, its mount, and the image architecture.</summary>
    private sealed record Nano11Target(BuildSession Session, string Mount, string Architecture);

    /// <summary>
    /// The Nano11 stage: the reductions nano11.ps1 performs beyond Core. Order matters. The component base is cleaned
    /// and reset first, packages and payloads go next, and the component store is replaced last, so a failure before
    /// the swap leaves the original WinSxS in place. Everything happens while the image is still mounted and a single
    /// commit carries it. Optional targets that are absent are skipped; only the retained families are required.
    /// </summary>
    private async Task TrimNano11Async(BuildSession s, CancellationToken token)
    {
        s.Stage(NanoStage, "Removing Nano11's additional apps, payloads, drivers, fonts, services and component families.", token);
        var target = new Nano11Target(s, s.Mount, s.Image.Architecture);
        s.Log("Nano11: this stage applies the plan, not the individual removal groups; the groups are Core's.");
        s.Log("Nano11: the output is not serviceable. Windows Update, recovery, Defender and component servicing are removed.");
        await CleanComponentsAsync(s, token).ConfigureAwait(false);
        await RemoveNano11AppsAsync(target, token).ConfigureAwait(false);
        await RemoveNano11PackagesAsync(target, token).ConfigureAwait(false);
        await s.Tracking.RunAsync("nano:files", NanoStage, "Remove Nano11 payloads, drivers, fonts and services", () =>
        {
            Nano11Files.Apply(target.Mount, s.Log, s.Recipe.TurboMode);
            Nano11Files.RemoveServices(target.Mount, s.Log);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await ReplaceNano11ComponentStoreAsync(s, target, token).ConfigureAwait(false);
        s.Log("Nano11: reductions applied. Test this image in an isolated VM; it is not a Windows 11 replacement.");
    }

    private static async Task RemoveNano11AppsAsync(Nano11Target target, CancellationToken token)
    {
        var s = target.Session;
        var output = await s.Dism.RunAsync(token, $"/Image:{target.Mount}", "/Get-ProvisionedAppxPackages").ConfigureAwait(false);
        var plan = Nano11Plan.AppFragments;
        var doomed = DismClient.ParseApps(output)
            .Where(app => plan.Any(fragment => app.PackageName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(app => app.PackageName).ToArray();
        if (doomed.Length == 0)
        {
            s.Tracking.Skip("nano:apps", NanoStage, "Remove Nano11 provisioned apps", "No matching provisioned app in this image");
            return;
        }
        foreach (var app in doomed)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await s.Tracking.RunAsync("nano:app:" + app.PackageName, NanoStage, $"Remove Nano11 app: {app.DisplayName}",
                    () => s.Dism.RunAsync(token, $"/Image:{target.Mount}", "/Remove-ProvisionedAppxPackage",
                        $"/PackageName:{app.PackageName}")).ConfigureAwait(false);
            }
            catch (DismCommandException error)
            {
                // A refused app is recorded as failed and reported; the rest of the plan still runs.
                s.Warn($"Nano11 could not unregister {app.DisplayName} ({app.PackageName}): exit {error.ExitCode}. " +
                    "The remaining reductions still run; any leftover folder is removed with the payloads.");
            }
        }
    }

    private static async Task RemoveNano11PackagesAsync(Nano11Target target, CancellationToken token)
    {
        var s = target.Session;
        var families = Nano11Plan.FeatureIdentities(target.Architecture);
        static bool Matches(string identity, IReadOnlyList<string> plan) =>
            plan.Any(family => identity.StartsWith(family, StringComparison.OrdinalIgnoreCase));
        var packages = ImageInventory.ParseIdentityStates(
                await s.Dism.RunAsync(token, $"/Image:{target.Mount}", "/Get-Packages").ConfigureAwait(false),
                "Package Identity", ImageItemKind.Package)
            .Where(item => Matches(item.Identity, families)).ToArray();
        var capabilities = ImageInventory.ParseIdentityStates(
                await s.Dism.RunAsync(token, $"/Image:{target.Mount}", "/Get-Capabilities").ConfigureAwait(false),
                "Capability Identity", ImageItemKind.Capability)
            .Where(item => Matches(item.Identity, families)).ToArray();
        if (packages.Length == 0 && capabilities.Length == 0)
        {
            s.Tracking.Skip("nano:packages", NanoStage, "Remove Nano11 packages and capabilities",
                "No matching package or capability in this image");
            return;
        }
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await s.Tracking.RunAsync("nano:package:" + package.Identity, NanoStage, $"Remove Nano11 package: {package.Identity}",
                    () => s.Dism.RunAsync(token, $"/Image:{target.Mount}", "/Remove-Package",
                        $"/PackageName:{package.Identity}", "/NoRestart")).ConfigureAwait(false);
            }
            catch (DismCommandException error)
            {
                // Protected packages refuse removal. Nano11 records the truth and keeps going; the payload work still applies.
                s.Warn($"Nano11 could not remove package {package.Identity}: exit {error.ExitCode}. Its component files are " +
                    "removed only if the component-store replacement keeps no family that needs them.");
            }
        }
        if (capabilities.Length == 0) return;
        var names = capabilities.Select(capability => capability.Identity).ToArray();
        await s.Tracking.RunBatchAsync(names.Select(name => ("nano:capability:" + name, $"Remove Nano11 capability: {name}")).ToArray(),
            NanoStage, async () => { await s.Dism.RemoveCapabilitiesAsync(target.Mount, names, token).ConfigureAwait(false); },
            "capabilities").ConfigureAwait(false);
    }

    /// <summary>
    /// Replace the image's component store with only the families Nano11 keeps. The retained folders are copied into a
    /// sibling folder inside the offline image and swapped in, so a failure before the swap leaves WinSxS intact.
    /// </summary>
    private static async Task ReplaceNano11ComponentStoreAsync(BuildSession s, Nano11Target target, CancellationToken token)
    {
        var store = Path.Combine(target.Mount, @"Windows\WinSxS");
        var temporary = Path.Combine(target.Mount, @"Windows\WinSxS_nano11");
        if (!Directory.Exists(store))
            throw new InvalidDataException("Nano11 cannot find this image's component store. No component payloads were pruned.");
        PathSafety.RejectReparsePoints(store);
        if (Directory.Exists(temporary)) ImageOperations.DeleteTree(temporary, s.Recipe.TurboMode);
        var directories = Directory.EnumerateDirectories(store)
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
        var wanted = Nano11Plan.RetainedStoreFamilies(target.Architecture);
        var missing = wanted.Where(family => !directories.Keys.Any(name => name.StartsWith(family, StringComparison.OrdinalIgnoreCase))).ToArray();
        // The runtime and servicing families are what keep the image able to start and to run its own servicing stack.
        // Without them this is not a smaller Windows but an unbootable one, so Nano11 refuses instead of guessing.
        if (missing.Length > 0)
            throw new InvalidDataException($"Nano11 cannot identify {missing.Length} required {target.Architecture} component " +
                $"famil{(missing.Length == 1 ? "y" : "ies")} in this image ({string.Join(", ", missing.Take(6))}" +
                (missing.Length > 6 ? ", ..." : "") + "). No component payloads were pruned and the image is unchanged.");

        await s.Tracking.RunAsync("nano:winsxs", NanoStage, "Replace the component store with only the retained families", () =>
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporary);
            long bytes = 0;
            var copied = 0;
            foreach (var (name, path) in directories.Where(pair => wanted.Any(family =>
                         pair.Key.StartsWith(family, StringComparison.OrdinalIgnoreCase))))
            {
                bytes += CopyTree(path, Path.Combine(temporary, name));
                copied++;
                s.Log($"Nano11: retained component family {name}");
            }
            foreach (var metadata in new[] { "Catalogs", "FileMaps", "Fusion", "InstallTemp", "Manifests", "SettingsManifests", "Temp" })
            {
                var path = Path.Combine(store, metadata);
                if (!Directory.Exists(path)) continue;
                bytes += CopyTree(path, Path.Combine(temporary, metadata));
                copied++;
            }
            if (copied == 0) throw new InvalidDataException("Nano11 copied no component families; the image was left unchanged.");
            s.Log($"Nano11: {directories.Count} store folders replaced by {copied} retained folders ({bytes:N0} bytes).");
            // The swap is the point of no return for the component store; it happens only after everything was copied.
            ImageOperations.DeleteTree(store, s.Recipe.TurboMode);
            Directory.Move(temporary, store);
            return Task.CompletedTask;
        }, () => "Component servicing is removed for this image; the original store was deleted after the retained families were copied.").ConfigureAwait(false);
        s.Log("Nano11: component store replaced with the retained runtime and servicing families.");
    }

    /// <summary>Copies a folder tree and returns the number of bytes copied.</summary>
    private static long CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(file, target, overwrite: true);
            bytes += new FileInfo(target).Length;
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            bytes += CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        return bytes;
    }
}
