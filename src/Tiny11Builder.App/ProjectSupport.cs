namespace Tiny11Builder.App;

internal enum SupportDestination { Sponsors, Kofi, Patreon, PayPal }

internal static class ProjectSupport
{
    public static string Purpose => UiText.FromEnglish("Help fund development time, testing across Windows releases and fixes for reported problems. All app features remain free.");

    public static Uri Link(SupportDestination destination) => new(destination switch
    {
        SupportDestination.Sponsors => "https://github.com/sponsors/ntdevlabs",
        SupportDestination.Kofi => "https://ko-fi.com/ntdev",
        SupportDestination.Patreon => "https://www.patreon.com/ntdev",
        SupportDestination.PayPal => "https://paypal.me/ntdev2",
        _ => throw new ArgumentOutOfRangeException(nameof(destination))
    });
}
