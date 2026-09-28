using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>
/// What is left running after she is powered off: a tiny, hidden copy of Raphael.exe (started with --standby) that does
/// nothing but watch your Telegram bot. When you message her name it starts the real Raphael and hands your message
/// over. It uses no voice, no Gemini and almost no CPU, and only your own Telegram account can wake her. The next
/// Raphael to start (however it was started) tells it to step aside, because only one program may read a bot's messages.
/// </summary>
public static class Standby
{
    private const string SingleInstanceName = @"Local\Raphael.Standby";
    private const string StopEventName = @"Local\Raphael.Standby.Stop";
    private static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(2);

    // Her name, in the ways you call her (the same words as her wake words), or Telegram's own /start.
    private static readonly Regex Wake = new(@"\b(raphael|sage)\b|ラファエル|^/start\b", RegexOptions.IgnoreCase);

    private static string? Token => Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim();

    private static bool TryGetOwner(out long ownerId)
    {
        ownerId = 0;
        return !string.IsNullOrEmpty(Token) && long.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_USER_ID")?.Trim(), out ownerId);
    }

    /// <summary>The full app calls this as it starts: any standby copy stops reading the bot and exits.</summary>
    public static void SignalStop()
    {
        try
        {
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
            stop.Set();
            Thread.Sleep(300); // let the standby copy notice it before this one starts polling
        }
        catch { /* nothing to stop */ }
    }

    /// <summary>The full app calls this when she powers off: leave a hidden standby copy behind (only if Telegram is set up).</summary>
    public static void Spawn()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !TryGetOwner(out _)) return;
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return; // a dev run

            Process.Start(new ProcessStartInfo(exe, "--standby") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not leave the standby watcher running: {ex.Message}");
        }
    }

    /// <summary>The body of "Raphael.exe --standby".</summary>
    public static async Task RunAsync()
    {
        if (!TryGetOwner(out var owner)) return;

        using var single = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst) return; // one is already watching

        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        using var cts = new CancellationTokenSource();
        _ = Task.Run(() => { stop.WaitOne(); cts.Cancel(); });
        var ct = cts.Token;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(70) };

        async Task<JsonNode?> Call(string method, JsonObject body)
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"https://api.telegram.org/bot{Token}/{method}", content, ct);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct); // the copy that just closed may still be reading the bot
            long offset = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var response = await Call("getUpdates", new JsonObject
                    {
                        ["offset"] = offset,
                        ["timeout"] = 25,
                        ["allowed_updates"] = new JsonArray { "message" }
                    });

                    if (response?["ok"]?.GetValue<bool>() != true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(8), ct);
                        continue;
                    }

                    foreach (var update in response["result"]!.AsArray())
                    {
                        var id = update!["update_id"]!.GetValue<long>();
                        var message = update["message"];
                        var fromId = message?["from"]?["id"]?.GetValue<long>() ?? 0;
                        var isPrivate = message?["chat"]?["type"]?.GetValue<string>() == "private";
                        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(message?["date"]?.GetValue<long>() ?? 0);

                        if (message == null || fromId != owner || !isPrivate || age > MaxMessageAge)
                        {
                            offset = id + 1;
                            continue;
                        }

                        var text = message["text"]?.GetValue<string>();
                        if (text != null && Wake.IsMatch(text))
                        {
                            // Mark everything before this message as handled, but leave this one unconfirmed so the
                            // real Raphael reads it (and answers it) when she starts.
                            await Call("getUpdates", new JsonObject { ["offset"] = id, ["timeout"] = 0, ["limit"] = 1 });
                            await Call("sendMessage", new JsonObject { ["chat_id"] = owner, ["text"] = "Summons received. Reactivating Raphael on your PC..." });
                            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--woken") { UseShellExecute = true });
                            return;
                        }

                        offset = id + 1;
                        await Call("sendMessage", new JsonObject
                        {
                            ["chat_id"] = owner,
                            ["text"] = "Raphael is powered off. Send a message with her name (for example \"Raphael\") to wake her."
                        });
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
