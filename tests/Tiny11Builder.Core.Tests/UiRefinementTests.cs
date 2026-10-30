using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class UiRefinementTests
{
    [Fact]
    public void Translated_expanders_refresh_their_accessible_names_with_their_headers()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace local = "using:Tiny11Builder.App";
        var expanders = document.Descendants().Where(element => element.Name.LocalName == "Expander")
            .Select(element => element.Attribute(local + "Translation.Properties")?.Value)
            .Where(properties => properties?.StartsWith("Header=", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(expanders);
        foreach (var properties in expanders)
        {
            var assignments = properties!.Split(';').Select(assignment => assignment.Split('=', 2))
                .ToDictionary(assignment => assignment[0], assignment => assignment[1], StringComparer.Ordinal);
            Assert.True(assignments.TryGetValue("AutomationProperties.Name", out var name),
                "Expander automation peers can retain their old header name after a language change.");
            Assert.Equal(assignments["Header"], name);
        }
    }

    [Fact]
    public void Sidebar_scrolls_at_small_heights_and_keeps_its_footer_at_the_bottom_when_there_is_room()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sidebar = Assert.Single(document.Descendants(ns + "ScrollViewer"), element => element.Attribute(x + "Name")?.Value == "SidebarScroller");
        Assert.Equal("Auto", sidebar.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", sidebar.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", sidebar.Attribute("HorizontalScrollMode")?.Value);
        Assert.Equal("Stretch", sidebar.Attribute("HorizontalContentAlignment")?.Value);
        var layout = Assert.Single(sidebar.Elements(ns + "Grid"));
        Assert.Equal("16,24,16,20", layout.Attribute("Padding")?.Value);
        Assert.Equal("{Binding ElementName=SidebarScroller, Path=ViewportHeight}", layout.Attribute("MinHeight")?.Value);
        Assert.Null(layout.Attribute("Height"));
        Assert.Equal(["Auto", "*", "Auto"], layout.Element(ns + "Grid.RowDefinitions")!.Elements(ns + "RowDefinition").Select(row => row.Attribute("Height")?.Value));
        foreach (var name in new[] { "StepRail", "ImageBadge", "ResumeButton", "RecoverButton", "ThemeChoice", "LanguageChoice", "SupportButton" })
            Assert.Single(sidebar.Descendants(), element => element.Attribute(x + "Name")?.Value == name);
        var footer = Assert.Single(layout.Elements(ns + "StackPanel"), element => element.Attribute("Grid.Row")?.Value == "2");
        Assert.Contains(footer.Descendants(), element => element.Attribute(x + "Name")?.Value == "LanguageChoice");
        Assert.Contains(footer.Descendants(), element => element.Attribute(x + "Name")?.Value == "SupportButton");
        var page = Assert.Single(document.Descendants(ns + "ScrollViewer"), element => element.Attribute(x + "Name")?.Value == "ContentScroller");
        Assert.DoesNotContain(sidebar, page.Ancestors());
        Assert.DoesNotContain(page, sidebar.Ancestors());
    }

    [Fact]
    public void The_first_rail_forward_step_is_available_but_uninspected_media_cannot_be_skipped()
    {
        Assert.True(StepNavigation.CanSelect(1, 0, 0, imageReady: false, expertReady: true));
        Assert.False(StepNavigation.CanSelect(2, 0, 0, imageReady: false, expertReady: true));
        Assert.False(StepNavigation.CanSelect(2, 1, 1, imageReady: false, expertReady: true));
        Assert.True(StepNavigation.CanSelect(2, 1, 1, imageReady: true, expertReady: true));
        Assert.Contains("Inspect an image", StepNavigation.Hint(1, imageReady: false, expertReady: true));
        Assert.Empty(StepNavigation.Hint(1, imageReady: true, expertReady: true));
    }

    [Fact]
    public void Server_images_cannot_unlock_the_component_step_even_when_their_architecture_and_build_match()
    {
        var server = new WindowsImage(1, "Windows Server 2025", "", "arm64", new Version(10, 0, 26100, 1), "ServerStandard", "Server Core", 1024);
        Assert.False(StepNavigation.CanSelect(2, 1, 1, server.IsSupported, true));
        Assert.True(StepNavigation.CanSelect(2, 1, 1, (server with { Installation = "Client", Edition = "Professional" }).IsSupported, true));
    }

    [Fact]
    public void Revisiting_steps_does_not_let_a_changed_image_or_unaccepted_advanced_actions_bypass_checks()
    {
        Assert.True(StepNavigation.CanSelect(1, 4, 4, imageReady: false, expertReady: false));
        Assert.False(StepNavigation.CanSelect(4, 1, 4, imageReady: false, expertReady: true));
        Assert.True(StepNavigation.CanSelect(4, 1, 4, imageReady: true, expertReady: true));
        Assert.False(StepNavigation.CanSelect(3, 2, 4, imageReady: true, expertReady: false));
        Assert.False(StepNavigation.CanSelect(5, 2, 2, imageReady: true, expertReady: true));
        Assert.Contains("advanced-removal", StepNavigation.Hint(2, imageReady: true, expertReady: false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Busy_completed_and_modal_states_do_not_accept_rail_navigation(bool busy, bool completed, bool modal)
    {
        Assert.False(StepNavigation.CanSelect(1, 0, 5, true, true, busy, completed, modal));
        Assert.False(StepNavigation.CanSelect(0, 4, 5, true, true, busy, completed, modal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public void Invalid_step_indices_are_never_reachable(int target) =>
        Assert.False(StepNavigation.CanSelect(target, 1, 5, true, true));

    [Theory]
    [InlineData(BuildMode.Tiny11)]
    [InlineData(BuildMode.Tiny11Core)]
    public void Preset_details_are_the_exact_full_catalog_set_without_claiming_image_availability(BuildMode mode)
    {
        var preview = PresetRemovalPreview.ForMode(mode);
        Assert.Equal(CleanupCatalog.ForMode(mode), preview.Select(option => option.Id));
        Assert.All(preview, option => Assert.Same(CleanupCatalog.Options.Single(source => source.Id == option.Id), option));
        Assert.Contains(preview, option => option.Id == "edge-files");
        Assert.Contains(preview, option => option.Id == "onedrive-files");
        Assert.Contains(preview, option => option.Kind == OptionKind.App && option.Targets.Length > 0);
        Assert.Contains(preview, option => option.Kind == OptionKind.Policy);
        Assert.Empty(PresetRemovalPreview.ForMode(BuildMode.Custom));
    }

    [Fact]
    public void Preset_policy_text_does_not_claim_Edge_is_kept_when_its_file_group_removes_it()
    {
        var edge = CleanupCatalog.Options.Single(option => option.Id == "edge");
        Assert.Contains("Preserve Edge", new OptionChoice(edge).Description);
        var combined = new OptionChoice(edge, presetRemovesEdgeFiles: true).Description;
        Assert.Contains("also removes Edge", combined);
        Assert.DoesNotContain("Preserve Edge", combined);
    }

    [Fact]
    public void Preset_view_has_no_editable_selection_controls_and_explains_conditional_removals()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var preview = Assert.Single(document.Descendants(), element => element.Attribute(x + "Name")?.Value == "PresetPreview");
        Assert.DoesNotContain(preview.Descendants(), element => element.Name.LocalName is "CheckBox" or "ToggleSwitch" or "TextBox");
        Assert.DoesNotContain(preview.Descendants().Attributes(), attribute => attribute.Name.LocalName == "Click");
        Assert.Contains(preview.Descendants().Attributes(), attribute => attribute.Value.Contains("not components confirmed present", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_includes_all_fourteen_thousand_components_not_the_filtered_or_selected_subset()
    {
        var choices = Enumerable.Range(0, 14053).Select(index => new InventoryChoice(new(
            (ImageItemKind)(index % 7), $"Full.Component.{index:D5}", $"Detail {index}", index % 2 == 0))).ToArray();
        choices[4].Selected = true;
        var filtered = choices.Where(choice => choice.Item.Kind == ImageItemKind.Service && choice.Selected).ToArray();
        Assert.True(filtered.Length < choices.Length);
        var exported = InventoryTextExport.Create(choices.Select(choice => choice.Item).ToArray());
        Assert.Contains("# Components: 14053", exported);
        Assert.Contains("Kind\tIdentity\tDetail\tProtected", exported);
        Assert.Contains("Full.Component.00000", exported);
        Assert.Contains("Full.Component.14052", exported);
        var rows = exported.Split(Environment.NewLine).Where(line => !line.StartsWith('#') && line.Contains('\t') && !line.StartsWith("Kind\t", StringComparison.Ordinal)).ToArray();
        Assert.Equal(14053, rows.Length);
        Assert.Equal(choices.Select(choice => choice.Item.Identity).Order(), rows.Select(row => row.Split('\t')[1]).Order());
    }

    [Fact]
    public void Export_preserves_full_identifiers_kinds_unicode_and_embedded_line_breaks()
    {
        var text = InventoryTextExport.Create([new(ImageItemKind.ComponentPayload, @"C:\Windows\组件\full.name", "a\tb\r\nc", true)]);
        Assert.Contains("ComponentPayload\tC:\\\\Windows\\\\组件\\\\full.name\ta\\tb\\r\\nc\tTrue", text);
        Assert.DoesNotContain("a\tb\r\nc", text);
    }

    [Fact]
    public void Relocalizing_choices_keeps_action_modes_selection_and_identifiers_unchanged()
    {
        var option = new OptionChoice(CleanupCatalog.Options[0]) { Selected = true };
        var item = new InventoryChoice(new(ImageItemKind.Feature, "Feature.Identity", "original detail", true)) { Selected = true, ModeIndex = 1 };
        var before = item.Action;
        var modes = item.Modes;
        option.RefreshLanguage();
        item.RefreshLanguage();
        item.ModeIndex = -1;
        Assert.True(option.Selected);
        Assert.True(item.Selected);
        Assert.Equal(before, item.Action);
        Assert.Same(modes, item.Modes);
        Assert.Equal("Feature.Identity", item.Title);
    }

    [Fact]
    public void Update_packages_preserve_order_and_can_be_moved_or_removed_without_changing_sources()
    {
        var selection = new UpdatePackageSelection();
        selection.Add(@"C:\updates\checkpoint.msu");
        selection.Add(@"C:\updates\target.CAB");
        Assert.Equal([@"C:\updates\checkpoint.msu", @"C:\updates\target.CAB"], selection.Paths);
        Assert.False(selection.Move(0, -1));
        Assert.True(selection.Move(1, -1));
        Assert.Equal("target.CAB", selection.Items[0].Name);
        Assert.True(selection.Move(0, 1));
        Assert.False(selection.Move(1, 1));
        Assert.True(selection.Remove(0));
        Assert.Equal([@"C:\updates\target.CAB"], selection.Paths);
        Assert.False(selection.Remove(-1));
    }

    [Theory]
    [InlineData(@"C:\updates\not-an-update.cap")]
    [InlineData(@"C:\updates\script.exe")]
    public void Update_picker_model_does_not_accept_cap_or_arbitrary_file_types(string path) =>
        Assert.Throws<InvalidDataException>(() => new UpdatePackageSelection().Add(path));

    [Fact]
    public void Update_restore_is_ordered_and_atomic_and_older_empty_recipes_remain_empty()
    {
        var selection = new UpdatePackageSelection();
        selection.Restore([@"C:\updates\one.msu", @"C:\updates\two.cab"]);
        var original = selection.Paths;
        Assert.Throws<InvalidDataException>(() => selection.Restore([@"C:\updates\invalid.cap"]));
        Assert.Equal(original, selection.Paths);
        Assert.Throws<InvalidDataException>(() => selection.Add(@"C:\UPDATES\.\one.MSU"));
        selection.Restore(new BuildRecipe().UpdatePackages);
        Assert.Empty(selection.Items);
    }
}

public sealed class LocalizationCatalogTests
{
    private static string Catalog(string id, string entries, string name = "Test") =>
        "{\"language\":\"" + id + "\",\"name\":\"" + name + "\",\"strings\":{" + entries + "}}";

    private static LanguageCatalog SmallEnglish => LanguageCatalog.Parse(Catalog("en", "\"Next\":\"Next\",\"Count\":\"{0:N0} of {1}\""));

    [Fact]
    public void Shipped_english_and_chinese_catalogs_are_complete_and_essential_text_is_really_translated()
    {
        var catalogs = LanguageCatalogs.Load(Path.Combine(AppContext.BaseDirectory, "Languages"));
        Assert.Empty(catalogs.Problems);
        var chinese = catalogs.Resolve("zh-Hans", CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("zh-Hans", chinese.Info.Id);
        Assert.Empty(chinese.MissingKeys);
        Assert.Equal(LanguageCatalogs.English.Keys.Order(), chinese.Keys.Order());
        Assert.True(chinese.Keys.Count() >= 500);
        Assert.Equal("下一步", chinese.Get("Common.Next"));
        Assert.Equal("欢迎", chinese.Get("Text.Welcome"));
        Assert.Contains("检查映像", chinese.Get("Text.InspectImage"));
        Assert.Contains("更新", chinese.Get("Text.WindowsUpdates"));
        Assert.Contains("不支持", chinese.Get("Core.ConsentBody"));
        Assert.Contains("恢复构建", chinese.Get("Resume.FrozenChoices"));
        Assert.Contains("微软", chinese.Get("Download.DefaultDescription"));
        Assert.All(CleanupCatalog.Options, option =>
        {
            Assert.True(chinese.Contains($"Option.{option.Id}.Title"));
            Assert.True(chinese.Contains($"Option.{option.Id}.Description"));
        });
    }

    [Fact]
    public void Missing_translations_are_explicit_english_fallbacks_never_blank()
    {
        var chinese = LanguageCatalog.Parse(Catalog("zh-Hans", "\"Next\":\"下一步\""), SmallEnglish);
        Assert.Equal("下一步", chinese.Get("Next"));
        Assert.Equal("{0:N0} of {1}", chinese.Get("Count"));
        Assert.Equal(["Count"], chinese.MissingKeys);
        Assert.Equal("[Unknown]", chinese.Get("Unknown"));
        Assert.Equal("English fallback", chinese.GetOrEnglish("NewKey", "English fallback"));
    }

    [Theory]
    [InlineData("{ invalid json")]
    [InlineData("[]")]
    [InlineData("{\"language\":\"zh-Hans\",\"name\":\"中文\",\"strings\":null}")]
    [InlineData("{\"language\":\"../outside\",\"name\":\"中文\",\"strings\":{\"Next\":\"下一步\"}}")]
    public void Invalid_external_language_documents_are_rejected(string json) =>
        Assert.ThrowsAny<Exception>(() => LanguageCatalog.Parse(json, SmallEnglish));

    [Theory]
    [InlineData("\"Unknown\":\"text\"")]
    [InlineData("\"Next\":\"\"")]
    [InlineData("\"Next\":\"   \"")]
    [InlineData("\"Next\":\"first\",\"Next\":\"second\"")]
    [InlineData("\"Count\":\"{0:N0}\"")]
    [InlineData("\"Count\":\"{0:N0} of {2}\"")]
    [InlineData("\"Count\":\"{0:N1} of {1}\"")]
    [InlineData("\"Count\":\"{0,1000000:N0} of {1}\"")]
    [InlineData("\"Count\":\"{0:N0} {1} {1}\"")]
    [InlineData("\"Count\":\"{0:N0\"")]
    public void Unknown_keys_empty_strings_duplicates_and_placeholder_drift_are_rejected(string entries) =>
        Assert.Throws<InvalidDataException>(() => LanguageCatalog.Parse(Catalog("zh-Hans", entries), SmallEnglish));

    [Fact]
    public void Translators_may_reorder_placeholders_and_text_is_not_interpreted_as_markup()
    {
        var catalog = LanguageCatalog.Parse(Catalog("zh-Hans", "\"Count\":\"共 {1} 项，已完成 {0:N0} 项\",\"Next\":\"<Button>下一步</Button>\""), SmallEnglish);
        Assert.Equal("共 4 项，已完成 3 项", catalog.Format("Count", 3, 4));
        Assert.Equal("<Button>下一步</Button>", catalog.Get("Next"));
        Assert.Throws<InvalidDataException>(() => LanguageCatalog.Parse(
            Catalog("zh-Hans", "\"Next\":\"下一步\"").Replace("\"strings\"", "\"link\":\"https://example.invalid\",\"strings\"", StringComparison.Ordinal), SmallEnglish));
    }

    [Fact]
    public void System_language_resolution_uses_supported_parents_and_an_english_last_fallback()
    {
        var catalogs = LanguageCatalogs.Load(Path.Combine(AppContext.BaseDirectory, "Languages"));
        Assert.Equal("zh-Hans", catalogs.Resolve("system", CultureInfo.GetCultureInfo("zh-CN")).Info.Id);
        Assert.Equal("en", catalogs.Resolve("system", CultureInfo.GetCultureInfo("fr-FR")).Info.Id);
        Assert.Equal("en", catalogs.Resolve("en", CultureInfo.GetCultureInfo("zh-CN")).Info.Id);
        Assert.Equal("zh-Hans", catalogs.Resolve("missing", CultureInfo.GetCultureInfo("zh-CN")).Info.Id);
        var serialized = JsonSerializer.Serialize(catalogs.Available);
        Assert.Equal(catalogs.Available, JsonSerializer.Deserialize<LanguageInfo[]>(serialized));
        Assert.Contains("English", serialized);
    }

    [Fact]
    public void Invalid_files_are_ignored_and_saved_language_preferences_survive_a_restart()
    {
        using var directory = new CatalogDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "broken.json"), "{ broken");
        File.WriteAllText(Path.Combine(directory.Path, "zh-Hans.json"), Catalog("zh-Hans", "\"Does.Not.Exist\":\"无效\""));
        var catalogs = LanguageCatalogs.Load(directory.Path);
        Assert.Equal(2, catalogs.Problems.Count);
        Assert.Equal("en", Assert.Single(catalogs.Available).Id);
        var preference = Path.Combine(directory.Path, "settings", "ui-language.json");
        Assert.Equal("system", LanguagePreference.Read(preference).Language);
        new LanguagePreference("zh-Hans").Write(preference);
        Assert.Equal("zh-Hans", LanguagePreference.Read(preference).Language);
        File.WriteAllText(preference, "{bad");
        Assert.Throws<JsonException>(() => LanguagePreference.Read(preference));
    }

    [Fact]
    public void All_static_xaml_translation_mappings_reference_valid_external_catalog_keys()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace local = "using:Tiny11Builder.App";
        var mappings = document.Descendants().Attributes(local + "Translation.Properties").ToArray();
        Assert.True(mappings.Length > 180);
        foreach (var mapping in mappings)
            foreach (var entry in mapping.Value.Split(';'))
                Assert.True(LanguageCatalogs.English.Contains(entry[(entry.IndexOf('=') + 1)..]), entry);
        var immutable = new[] { "Tiny11", "Tiny11 Core", "Tiny11 Builder", "SHA-256", "Patreon", "PayPal" };
        var properties = new[] { "Text", "Header", "Content", "Title", "Message", "PlaceholderText", "OffContent", "OnContent", "CloseButtonText", "AutomationProperties.Name", "AutomationProperties.HelpText" };
        foreach (var element in document.Descendants())
            foreach (var attribute in element.Attributes().Where(attribute => properties.Contains(attribute.Name.LocalName)))
            {
                var value = attribute.Value;
                if (value.StartsWith('{') || !value.Any(char.IsLetter) || immutable.Contains(value)) continue;
                Assert.Contains(attribute.Name.LocalName + "=", element.Attribute(local + "Translation.Properties")?.Value ?? "");
            }
    }

    private sealed class CatalogDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "CatalogTestData", Guid.NewGuid().ToString("N"));
        public CatalogDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
