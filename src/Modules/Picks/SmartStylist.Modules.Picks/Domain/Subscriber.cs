namespace SmartStylist.Modules.Picks.Domain;

/// <summary>Where one person (a Wardrobe style profile) gets their picks. Today only Telegram.</summary>
public sealed class Subscriber
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>Random code in the person's Telegram link (t.me/bot?start=CODE). Opening it links their chat.</summary>
    public required string LinkCode { get; set; }

    /// <summary>The Telegram chat that pressed Start with <see cref="LinkCode"/>; null until then or after /stop.</summary>
    public long? TelegramChatId { get; set; }

    public DateTimeOffset? LinkedAt { get; set; }
}
