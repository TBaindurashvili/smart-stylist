using System.Text.RegularExpressions;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Recognises a shop's bot-protection page so the collector can stop and say who blocked it.
/// The app never tries to solve a challenge or CAPTCHA: it reports the block and moves on.
/// </summary>
public static partial class BotWall
{
    /// <summary>The bot-protection vendor behind a blocked response, or null when the page looks like a normal page.</summary>
    public static string? Detect(int status, IReadOnlyDictionary<string, string> headers, string? html)
    {
        string? Header(string name) =>
            headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        var body = html is null ? "" : html.Length > 200_000 ? html[..200_000] : html;
        var server = Header("server") ?? "";
        var blockedStatus = status is 401 or 403 or 405 or 418 or 429 or 503;

        if (Header("x-datadome") is not null || body.Contains("captcha-delivery.com", StringComparison.OrdinalIgnoreCase))
        {
            return "DataDome";
        }

        if (body.Contains("px-captcha", StringComparison.OrdinalIgnoreCase) || PerimeterX().IsMatch(body))
        {
            return "PerimeterX (HUMAN)";
        }

        if (Header("x-kpsdk-ct") is not null || body.Contains("KPSDK", StringComparison.Ordinal))
        {
            return "Kasada";
        }

        if (body.Contains("_Incapsula_Resource", StringComparison.OrdinalIgnoreCase) || Header("x-iinfo") is not null)
        {
            return "Imperva (Incapsula)";
        }

        if (body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Just a moment...", StringComparison.Ordinal)
            || (blockedStatus && (Header("cf-ray") is not null || server.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))))
        {
            return "Cloudflare";
        }

        if (body.Contains("Pardon Our Interruption", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Shape Security", StringComparison.OrdinalIgnoreCase))
        {
            return "F5 Shape / Distil";
        }

        if (blockedStatus && (server.Contains("AkamaiGHost", StringComparison.OrdinalIgnoreCase)
            || body.Contains("errors.edgesuite.net", StringComparison.OrdinalIgnoreCase)
            || (body.Contains("Access Denied", StringComparison.Ordinal) && body.Contains("Reference #", StringComparison.Ordinal))))
        {
            return "Akamai";
        }

        if (blockedStatus && CaptchaWords().IsMatch(body))
        {
            return "an unnamed bot check";
        }

        return null;
    }

    [GeneratedRegex(@"_pxAppId|perimeterx|px-cdn\.net|/_px\d?/", RegexOptions.IgnoreCase)]
    private static partial Regex PerimeterX();

    [GeneratedRegex(@"captcha|are you a robot|verify you are human|access denied|request unsuccessful", RegexOptions.IgnoreCase)]
    private static partial Regex CaptchaWords();
}

/// <summary>The shop answered with a bot-protection page. Not retried and never solved.</summary>
public sealed class BlockedByBotWallException(string vendor, int status, Uri url)
    : HttpRequestException($"Blocked by {vendor} (HTTP {status}) at {url.Host}. Not solving it; use a feed, email or the extension for this shop.")
{
    public string Vendor { get; } = vendor;

    public int Status { get; } = status;
}
