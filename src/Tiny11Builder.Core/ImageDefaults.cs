namespace Tiny11Builder.Core;

public static class ImageDefaults
{
    public static int SelectPosition(IReadOnlyList<WindowsImage> images, int? restoredIndex = null)
    {
        if (images.Count == 0) throw new InvalidDataException("The source contains no Windows editions.");
        if (restoredIndex is int saved)
            for (var i = 0; i < images.Count; i++)
                if (images[i].Index == saved) return i;
        for (var i = 0; i < images.Count; i++)
            if (images[i].Edition.Equals("Professional", StringComparison.OrdinalIgnoreCase) ||
                images[i].Name.Equals("Windows 11 Pro", StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < images.Count; i++) if (images[i].Index == 1) return i;
        throw new InvalidDataException("Neither Windows 11 Pro nor image index 1 exists in this source.");
    }
}
