using System.Globalization;
using System.Net;
using System.Text;

namespace SmartStylist.Modules.Picks;

/// <summary>Writes picks as a Telegram message (HTML): what, the price, and the reason for price, style and size.</summary>
public static class PickMessage
{
    /// <summary>More than this would be a catalogue, not a pick; the rest are named by count.</summary>
    public const int MaxPerMessage = 8;

    public static string Telegram(string person, IReadOnlyList<Pick> picks)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"<b>{(picks.Count == 1 ? "1 pick" : $"{picks.Count} picks")} for {E(person)}</b>\n");
        text.Append("Each is in your style, in your size, at a price near its real low.\n");
        foreach (var (pick, i) in picks.Take(MaxPerMessage).Select((p, i) => (p, i + 1)))
        {
            var deal = pick.Deal;
            var usual = deal.Assessment.UsualPrice is { } u ? $" (usually {Money(u, deal.Currency)})" : "";
            text.Append(CultureInfo.InvariantCulture, $"\n{i}. <a href=\"{E(deal.Url)}\">{E(deal.Title)}</a>\n");
            text.Append(CultureInfo.InvariantCulture, $"{E(pick.Brand)} · {E(deal.ShopName)} · <b>{Money(deal.Price, deal.Currency)}</b>{usual}\n");
            text.Append(CultureInfo.InvariantCulture, $"Price: {E(deal.Assessment.Reason)}\n");
            text.Append(CultureInfo.InvariantCulture, $"Style: {E(pick.Style.Reason)}\n");
            text.Append(CultureInfo.InvariantCulture, $"Size: {E(pick.Size.Reason)}\n");
        }

        if (picks.Count > MaxPerMessage)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n…and {picks.Count - MaxPerMessage} more on the Picks page.\n");
        }

        return text.ToString();
    }

    private static string Money(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:0.00} {currency}").TrimEnd();

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
