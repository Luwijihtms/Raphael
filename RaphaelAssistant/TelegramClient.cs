using System.Text;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// Lets one specific Telegram account command Raphael from a phone: text or voice notes in, chat text out.
/// It only makes outgoing requests (long polling), so nothing on your PC or router is exposed.
/// Configured with TELEGRAM_BOT_TOKEN (from @BotFather) and TELEGRAM_USER_ID (who may use it).
/// </summary>
public sealed class TelegramClient : IDisposable
{
    private const int MaxVoiceSeconds = 90;
    private const long MaxFileBytes = 8_000_000;
    private static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(70) };
    private readonly string _token;
    private readonly long _allowedUserId;
    private readonly Func<string, byte[]?, string, Task<string>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string? BotUsername { get; private set; }

    private TelegramClient(string token, long allowedUserId, Func<string, byte[]?, string, Task<string>> handler)
    {
        _token = token;
        _allowedUserId = allowedUserId;
        _handler = handler;
    }

    /// <param name="handler">Receives (text, audio, audioMimeType) and returns the chat reply.</param>
    /// <returns>Null when Telegram isn't configured.</returns>
    public static TelegramClient? Create(Func<string, byte[]?, string, Task<string>> handler)
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim();
        if (string.IsNullOrEmpty(token)) return null;

        if (!long.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_USER_ID")?.Trim(), out var userId))
        {
            Console.WriteLine("Telegram: TELEGRAM_USER_ID is not set, so phone control is off (it must be locked to your account).");
            return null;
        }
        return new TelegramClient(token, userId, handler);
    }

    /// <summary>
    /// A few retries with backoff, not just one try: right after Raphael starts, Windows' network/DNS can still be coming
    /// up for a few seconds (especially without --startup's own wait), and one failed check here used to mean Telegram
    /// stayed disconnected for the whole session with no further sign of trying.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        JsonNode? me = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            me = await CallAsync("getMe", null, _cts.Token);
            if (me?["ok"]?.GetValue<bool>() == true) break;

            if (attempt < 4)
            {
                var wait = TimeSpan.FromSeconds(3 * (attempt + 1));
                Console.WriteLine($"Telegram: could not reach the bot yet (attempt {attempt + 1}/5); retrying in {wait.TotalSeconds:0}s...");
                try { await Task.Delay(wait, _cts.Token); } catch (OperationCanceledException) { return false; }
            }
        }

        if (me?["ok"]?.GetValue<bool>() != true)
        {
            Console.WriteLine("Telegram: could not reach the bot after several tries. Check the token and your connection.");
            return false;
        }

        BotUsername = me["result"]?["username"]?.GetValue<string>();
        _loop = Task.Run(PollLoopAsync);
        return true;
    }

    private async Task PollLoopAsync()
    {
        long offset = 0;
        var backoff = TimeSpan.FromSeconds(2);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var response = await CallAsync("getUpdates", new JsonObject
                {
                    ["offset"] = offset,
                    ["timeout"] = 30,
                    ["allowed_updates"] = new JsonArray { "message" }
                }, _cts.Token);

                if (response?["ok"]?.GetValue<bool>() != true)
                {
                    var code = response?["error_code"]?.GetValue<int>();
                    Console.WriteLine(code == 409
                        ? "Telegram: another copy of Raphael is already reading this bot's messages; close the other one."
                        : $"Telegram: getUpdates failed ({code?.ToString() ?? "no response"}); retrying.");
                    await Task.Delay(backoff, _cts.Token);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                    continue;
                }

                backoff = TimeSpan.FromSeconds(2);
                foreach (var update in response["result"]!.AsArray())
                {
                    offset = update!["update_id"]!.GetValue<long>() + 1;
                    try
                    {
                        await HandleMessageAsync(update["message"]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Telegram: failed to handle a message: {Redact(ex.Message)}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram: connection problem ({Redact(ex.Message)}); retrying.");
                try { await Task.Delay(backoff, _cts.Token); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task HandleMessageAsync(JsonNode? message)
    {
        if (message == null) return;

        var fromId = message["from"]?["id"]?.GetValue<long>() ?? 0;
        var chatId = message["chat"]?["id"]?.GetValue<long>() ?? 0;
        var isPrivate = message["chat"]?["type"]?.GetValue<string>() == "private";

        // Anyone else who finds the bot gets nothing.
        if (fromId != _allowedUserId || !isPrivate)
        {
            Console.WriteLine($"[telegram] ignored a message from an unauthorised user ({fromId})");
            return;
        }

        // Don't act on commands that piled up while Raphael was switched off.
        var sent = DateTimeOffset.FromUnixTimeSeconds(message["date"]?.GetValue<long>() ?? 0);
        if (DateTimeOffset.UtcNow - sent > MaxMessageAge) return;

        var text = message["text"]?.GetValue<string>();
        var voice = message["voice"] ?? message["audio"];

        if (text != null && text.StartsWith("/start"))
        {
            await SendMessageAsync(chatId, "Raphael is online. Type a command, or hold the microphone button and speak.");
            return;
        }

        byte[]? audio = null;
        var mime = "audio/ogg";
        if (voice != null)
        {
            if ((voice["duration"]?.GetValue<int>() ?? 0) > MaxVoiceSeconds)
            {
                await SendMessageAsync(chatId, $"That voice message is too long. Please keep it under {MaxVoiceSeconds} seconds.");
                return;
            }
            mime = voice["mime_type"]?.GetValue<string>() ?? (message["voice"] != null ? "audio/ogg" : "audio/mpeg");
            audio = await DownloadFileAsync(voice["file_id"]!.GetValue<string>());
            if (audio == null)
            {
                await SendMessageAsync(chatId, "I could not download that voice message.");
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            await SendMessageAsync(chatId, "I can only read text and voice messages.");
            return;
        }

        Console.WriteLine(audio != null ? "[telegram] (voice message)" : $"[telegram] {text}");
        _ = CallAsync("sendChatAction", new JsonObject { ["chat_id"] = chatId, ["action"] = "typing" }, _cts.Token);

        var reply = await _handler(text ?? "", audio, mime);
        await SendMessageAsync(chatId, reply);
    }

    /// <summary>Sends a message to the one allowed user (for timers that finish, and the like). Their private chat id is their user id.</summary>
    public Task SendToOwnerAsync(string text) => SendMessageAsync(_allowedUserId, text);

    private async Task SendMessageAsync(long chatId, string text)
    {
        if (text.Length > 4000) text = text[..4000] + "...";
        await CallAsync("sendMessage", new JsonObject { ["chat_id"] = chatId, ["text"] = text }, _cts.Token);
    }

    private async Task<byte[]?> DownloadFileAsync(string fileId)
    {
        var info = await CallAsync("getFile", new JsonObject { ["file_id"] = fileId }, _cts.Token);
        var path = info?["result"]?["file_path"]?.GetValue<string>();
        var size = info?["result"]?["file_size"]?.GetValue<long>() ?? 0;
        if (path == null || size > MaxFileBytes) return null;

        return await _http.GetByteArrayAsync($"https://api.telegram.org/file/bot{_token}/{path}", _cts.Token);
    }

    /// <summary>Calls a Bot API method. Telegram answers errors with a JSON body too, so that is returned as-is.</summary>
    private async Task<JsonNode?> CallAsync(string method, JsonObject? body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"https://api.telegram.org/bot{_token}/{method}", content, ct);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Request URLs contain the token, so never let it reach the console.
            Console.WriteLine($"Telegram: {method} failed ({Redact(ex.Message)})");
            return null;
        }
    }

    private string Redact(string text) => text.Replace(_token, "<token>");

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
