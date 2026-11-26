using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CtrlAltDaze.Core.Telegram;

/// <summary>A chat a bot can write to: its id as Telegram's <c>chat_id</c> takes it, and words for the person.</summary>
public sealed record TelegramChat(string Id, string Title);

/// <summary>A Telegram call that didn't work, as a plain sentence that never holds the bot's key (D-158).</summary>
public sealed class TelegramException(string message) : Exception(message);

/// <summary>
/// The Telegram Bot API calls the app makes (D-158): <c>getMe</c> and <c>getUpdates</c> while the editor's
/// "Set up Telegram" guide is open, <c>sendMessage</c> for the step. The key rides in the address, so no address,
/// request or server text ever reaches an exception, a log or YAML.
/// </summary>
public sealed partial class TelegramApi(HttpClient http)
{
    /// <summary>Telegram's own limit for one message (after formatting, which we never send).</summary>
    public const int MaxText = 4096;

    public const string BotFatherUrl = "https://t.me/BotFather";

    /// <summary><c>123456789:AA…</c>: the bot's number, a colon, then the secret part (BotFather's format).</summary>
    public static bool LooksLikeKey(string key) => KeyShape().IsMatch(key);

    /// <summary>A private chat's number, a group's negative number, or a public channel's <c>@name</c>.</summary>
    public static bool LooksLikeChat(string chat) => ChatShape().IsMatch(chat);

    /// <summary>16 letters and digits for <c>?start=</c> (Telegram allows 64 of <c>A-Za-z0-9_-</c>).</summary>
    public static string NewStartCode() =>
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 16);

    /// <summary>Opens the bot's chat with a Start button that sends <c>/start &lt;code&gt;</c>.</summary>
    public static string StartLink(string botName, string code) => $"https://t.me/{botName}?start={code}";

    /// <summary>Offers the bot to one of the person's groups; the group's first message is <c>/start@bot &lt;code&gt;</c>.</summary>
    public static string GroupLink(string botName, string code) => $"https://t.me/{botName}?startgroup={code}";

    /// <summary>The bot's user name (without @); fails when Telegram doesn't know the key.</summary>
    public async Task<string> BotNameAsync(string key, CancellationToken cancellationToken)
    {
        using var json = await CallAsync(key, "getMe", null, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("result").TryGetProperty("username", out var name) && name.GetString() is { Length: > 0 } text
            ? text
            : throw new TelegramException("Telegram didn't say the bot's name. Try again in a moment.");
    }

    /// <summary>
    /// One long poll (at most <paramref name="waitSeconds"/>): the chat whose message is <c>/start &lt;code&gt;</c>, or null.
    /// <paramref name="offset"/> moves past what was read, so the next poll waits for new messages only.
    /// </summary>
    public async Task<TelegramChat?> FindStartAsync(string key, string code, Ref<long> offset, int waitSeconds, CancellationToken cancellationToken)
    {
        var body = $"{{\"offset\":{offset.Value.ToString(CultureInfo.InvariantCulture)},\"timeout\":{waitSeconds.ToString(CultureInfo.InvariantCulture)},\"allowed_updates\":[\"message\"]}}";
        using var json = await CallAsync(key, "getUpdates", body, cancellationToken).ConfigureAwait(false);
        var (chat, next) = FindStart(json.RootElement, code);
        if (next > offset.Value)
            offset.Value = next;
        return chat;
    }

    /// <summary>Sends <paramref name="text"/> as plain text (no Markdown: a value from an earlier step must never break it).</summary>
    public async Task SendAsync(string key, string chat, string text, bool silent, CancellationToken cancellationToken)
    {
        using var json = await CallAsync(key, "sendMessage", SendBody(chat, text, silent), cancellationToken).ConfigureAwait(false);
    }

    internal static string SendBody(string chat, string text, bool silent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (long.TryParse(chat, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
                writer.WriteNumber("chat_id", id);
            else
                writer.WriteString("chat_id", chat);
            writer.WriteString("text", text);
            if (silent)
                writer.WriteBoolean("disable_notification", true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The chat that sent <c>/start code</c> (or <c>/start@bot code</c> in a group) and the offset past every update read.</summary>
    internal static (TelegramChat? Chat, long Next) FindStart(JsonElement root, string code)
    {
        TelegramChat? found = null;
        long next = 0;
        if (!root.TryGetProperty("result", out var updates) || updates.ValueKind != JsonValueKind.Array)
            return (null, 0);
        foreach (var update in updates.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var updateId) && updateId.TryGetInt64(out var number))
                next = Math.Max(next, number + 1);
            if (found is not null || !update.TryGetProperty("message", out var message)
                || !message.TryGetProperty("text", out var textValue) || textValue.GetString() is not { } text
                || !message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatId) || !chatId.TryGetInt64(out var id))
                continue;
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && (parts[0] == "/start" || parts[0].StartsWith("/start@", StringComparison.Ordinal)) && parts[1] == code)
                found = new TelegramChat(id.ToString(CultureInfo.InvariantCulture), ChatTitle(chat));
        }
        return (found, next);
    }

    private static string ChatTitle(JsonElement chat)
    {
        string? Text(string name) => chat.TryGetProperty(name, out var value) ? value.GetString() : null;
        var type = Text("type");
        if (type is "group" or "supergroup")
            return $"{Text("title") ?? "A group"} (group)";
        var name = string.Join(' ', new[] { Text("first_name"), Text("last_name") }.Where(n => n is { Length: > 0 }));
        return name.Length > 0 ? $"{name} (private chat)" : "Private chat";
    }

    private async Task<JsonDocument> CallAsync(string key, string method, string? body, CancellationToken cancellationToken)
    {
        if (!LooksLikeKey(key))
            throw new TelegramException("That isn't a Telegram bot key. BotFather's keys look like 123456789:AAE…");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://api.telegram.org/bot{key}/{method}"))
        {
            Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The exception's text can hold the address, and the address holds the key.
            throw new TelegramException("Couldn't reach Telegram. Check that this PC is online.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TelegramException("Telegram didn't answer in time.");
        }
        using (response)
        {
            JsonDocument? json = null;
            try
            {
                json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (JsonException)
            {
            }
            if (response.IsSuccessStatusCode && json is not null && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return json;
            var description = json is not null && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            json?.Dispose();
            throw new TelegramException(Failure(response.StatusCode, description));
        }
    }

    /// <summary>Our own sentence for each answer Telegram gives; its description is only matched, never shown (D-031's rule).</summary>
    internal static string Failure(HttpStatusCode status, string description) => (int)status switch
    {
        401 or 404 => "Telegram doesn't know this bot key. Copy it again from BotFather (/mybots, then API Token).",
        403 when description.Contains("kicked", StringComparison.OrdinalIgnoreCase) || description.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            => "The bot was blocked or removed from this chat. Unblock it (or add it back) in Telegram.",
        403 => "The bot can't write to this chat yet. Open the bot in Telegram and press Start (or add it to the group).",
        400 when description.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
            => "Telegram can't find this chat. Run Set up Telegram again, or press Start in the bot's chat.",
        400 when description.Contains("too long", StringComparison.OrdinalIgnoreCase) => $"The message is too long: Telegram takes at most {MaxText:N0} characters.",
        400 when description.Contains("empty", StringComparison.OrdinalIgnoreCase) => "The message is empty.",
        409 => "This bot sends its messages to a web address (a webhook). Make a new bot with BotFather for this PC.",
        429 => "Telegram says too many messages were sent. Wait a minute and try again.",
        >= 500 => "Telegram isn't working right now. Try again later.",
        var code => $"Telegram refused the message ({code}).",
    };

    [GeneratedRegex(@"^\d{3,15}:[A-Za-z0-9_-]{30,64}$")]
    private static partial Regex KeyShape();

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z][A-Za-z0-9_]{3,31})$")]
    private static partial Regex ChatShape();
}

/// <summary>A value an async method can update for its caller (async methods can't take <c>ref</c>).</summary>
public sealed class Ref<T>(T value)
{
    public T Value { get; set; } = value;
}
