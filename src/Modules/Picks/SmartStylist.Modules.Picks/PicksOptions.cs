namespace SmartStylist.Modules.Picks;

public sealed class PicksOptions
{
    public const string SectionName = "Picks";

    /// <summary>When the daily picks go out, in UTC. After the morning price collection and feed import.</summary>
    public string Cron { get; set; } = "30 6 * * *";

    public TelegramOptions Telegram { get; set; } = new();
}

public sealed class TelegramOptions
{
    /// <summary>From @BotFather. A secret: set it with user-secrets (Picks:Telegram:BotToken), never in appsettings.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>The bot's username without @, for the t.me link people open to get their picks.</summary>
    public string BotName { get; set; } = "";
}
