using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Picks.Domain;
using SmartStylist.Modules.Picks.Persistence;

namespace SmartStylist.Modules.Picks.Telegram;

/// <summary>
/// The Telegram side of picks: each person gets a link to the bot with their own code; pressing Start there links
/// their chat to their profile, and /stop unlinks it. Uses the Bot API's getUpdates, so no public web address is needed.
/// </summary>
public sealed class TelegramBot(HttpClient http, IOptions<PicksOptions> options, PicksDbContext db, TimeProvider time, ILogger<TelegramBot> logger)
{
    private TelegramOptions Settings => options.Value.Telegram;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings.BotToken);

    /// <summary>The t.me link that links a person's Telegram chat to their profile. Null until the bot name is set.</summary>
    public async Task<string?> LinkAsync(long profileId, CancellationToken cancellationToken)
    {
        var subscriber = await db.Subscribers.FirstOrDefaultAsync(s => s.ProfileId == profileId, cancellationToken);
        if (subscriber is null)
        {
            subscriber = db.Subscribers.Add(new Subscriber { ProfileId = profileId, LinkCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) }).Entity;
            await db.SaveChangesAsync(cancellationToken);
        }

        return string.IsNullOrWhiteSpace(Settings.BotName) ? null : $"https://t.me/{Settings.BotName.TrimStart('@')}?start={subscriber.LinkCode}";
    }

    /// <summary>Reads what people sent the bot since the last call: links chats on /start CODE, unlinks on /stop.</summary>
    /// <returns>How many chats were linked or unlinked.</returns>
    public async Task<int> SyncLinksAsync(CancellationToken cancellationToken)
    {
        var updates = await CallAsync<Update[]>("getUpdates", new { timeout = 0, allowed_updates = new[] { "message" } }, cancellationToken) ?? [];
        var changed = 0;
        foreach (var message in updates.Select(u => u.Message).OfType<Message>())
        {
            var words = (message.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = words.FirstOrDefault()?.Split('@')[0];
            if (command == "/start" && words.Length > 1
                && await db.Subscribers.FirstOrDefaultAsync(s => s.LinkCode == words[1], cancellationToken) is { } subscriber)
            {
                subscriber.TelegramChatId = message.Chat.Id;
                subscriber.LinkedAt = time.GetUtcNow();
                changed++;
                await SendAsync(message.Chat.Id, "You're in. I'll message you only when something is really worth buying for you: your style, your size, at a price near its real low.", cancellationToken);
            }
            else if (command == "/stop")
            {
                foreach (var linked in await db.Subscribers.Where(s => s.TelegramChatId == message.Chat.Id).ToListAsync(cancellationToken))
                {
                    linked.TelegramChatId = null;
                    changed++;
                }

                await SendAsync(message.Chat.Id, "Stopped. Open your link again to get picks back.", cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (updates.Length > 0)
        {
            // Confirms everything read so far, so the next call only sees new messages.
            await CallAsync<Update[]>("getUpdates", new { offset = updates.Max(u => u.UpdateId) + 1, limit = 1, timeout = 0 }, cancellationToken);
        }

        return changed;
    }

    /// <summary>Sends one message (Telegram HTML), without link previews.</summary>
    public Task SendAsync(long chatId, string html, CancellationToken cancellationToken) =>
        CallAsync<Message>("sendMessage", new
        {
            chat_id = chatId,
            text = html,
            parse_mode = "HTML",
            link_preview_options = new { is_disabled = true },
        }, cancellationToken);

    private async Task<T?> CallAsync<T>(string method, object body, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Telegram is not set up: set Picks:Telegram:BotToken with dotnet user-secrets (see README).");
        }

        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{Settings.BotToken}/{method}", body, cancellationToken);
        var reply = await response.Content.ReadFromJsonAsync<Reply<T>>(cancellationToken);
        if (reply is not { Ok: true })
        {
            // Never log the request URL: it holds the token.
            logger.LogWarning("Telegram {Method} failed: {Status} {Description}", method, (int)response.StatusCode, reply?.Description);
            throw new InvalidOperationException($"Telegram {method} failed: {reply?.Description ?? response.StatusCode.ToString()}");
        }

        return reply.Result;
    }

    private sealed record Reply<T>([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("result")] T? Result,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record Update([property: JsonPropertyName("update_id")] long UpdateId, [property: JsonPropertyName("message")] Message? Message);

    private sealed record Message([property: JsonPropertyName("chat")] Chat Chat, [property: JsonPropertyName("text")] string? Text);

    private sealed record Chat([property: JsonPropertyName("id")] long Id);
}
