using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>
/// Instant replies for simple actions that worked ("Notepad launched", "volume set to 60"), written here instead of by
/// Gemini. A tool call normally costs two Gemini round-trips: one to decide what to run, one to phrase the answer. For
/// these plain confirmations the second one adds seconds and nothing else, so it is skipped. Anything that isn't a clear
/// success, and anything whose result carries information (mail, calendar, scans, Wordle, timers, questions), still goes
/// to Gemini. RAPHAEL_FASTREPLY=off turns this off.
/// </summary>
public static class LocalReplies
{
    private static readonly Random Rng = new();

    public static bool Enabled => !string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_FASTREPLY"), "off", StringComparison.OrdinalIgnoreCase);

    private static T Pick<T>(params T[] options) => options[Rng.Next(options.Length)];

    /// <summary>The reply for one finished tool call, or null when Gemini should phrase it.</summary>
    public static (string Ja, string En)? TryBuild(ToolCall call, string result)
    {
        result = (result ?? "").Trim();
        if (result.Length == 0) return null;

        switch (call.Name)
        {
            case "open_app":
            {
                var m = Regex.Match(result, @"^Launched (.+)\.$");
                if (!m.Success) return null;
                var name = m.Groups[1].Value;
                return Pick(
                    ($"告。{name}を起動しました。", $"Notice. {name} launched."),
                    ($"告。{name}の起動が完了しました。実行結果：成功。", $"Notice. Launch of {name} complete. Execution result: success."));
            }

            case "close_app":
            {
                var m = Regex.Match(result, @"^Closed (.+)\.$");
                if (!m.Success) return null; // a forced close or a failure still needs Gemini's explanation
                var name = m.Groups[1].Value;
                return Pick(
                    ($"告。{name}を終了しました。", $"Notice. {name} closed."),
                    ($"告。{name}の終了処理が完了しました。実行結果：成功。", $"Notice. {name} has been closed. Execution result: success."));
            }

            case "open_url":
            {
                var m = Regex.Match(result, @"^Opened (https?://\S+?)\.?$");
                if (!m.Success || !Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var uri)) return null;
                var host = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
                return Pick(
                    ($"告。{host}を開きました。", $"Notice. Opened {host}."),
                    ($"告。{host}のページを表示しました。実行結果：成功。", $"Notice. The page at {host} is open. Execution result: success."));
            }

            case "play_playlist":
            {
                var m = Regex.Match(result, @"^Now playing the playlist '(.+)' \(");
                if (!m.Success) return null;
                return ($"告。プレイリスト「{m.Groups[1].Value}」の再生を開始しました。", $"Notice. {result}");
            }

            case "play_song":
            {
                var m = Regex.Match(result, @"^Now playing the single track '(.+)' by (.+)\.$");
                if (!m.Success) return null;
                return ($"告。{m.Groups[2].Value}の「{m.Groups[1].Value}」の再生を開始しました。", $"Notice. {result}");
            }

            case "spotify_control":
            case "media_control":
                return Control(result);

            case "system_volume":
                return SystemVolumeReply(result);

            default:
                return null;
        }
    }

    /// <summary>Playback controls: only the exact success messages the player code produces.</summary>
    private static (string Ja, string En)? Control(string result)
    {
        var volume = Regex.Match(result, @"^Spotify volume set to (\d+) percent\.$");
        if (volume.Success)
            return ($"告。音量を{volume.Groups[1].Value}パーセントに設定しました。実行結果：成功。", $"Notice. {result} Execution result: success.");

        string? ja = null;
        if (result == "Paused." || Regex.IsMatch(result, @"^Paused .+\.$")) ja = "再生を一時停止しました。";
        else if (result == "Resumed playback." || Regex.IsMatch(result, @"^Resumed .+\.$")) ja = "再生を再開しました。";
        else if (result.StartsWith("Skipped to the next ")) ja = "次の曲へ移行しました。";
        else if (result.StartsWith("Went back to the previous ")) ja = "前の曲へ戻りました。";
        else if (result.StartsWith("Toggled play and pause")) ja = "再生状態を切り替えました。";
        else if (result == "Shuffle is on.") ja = "シャッフルを有効にしました。";
        else if (result == "Shuffle is off.") ja = "シャッフルを無効にしました。";
        else if (result == "Repeat is off.") ja = "リピートを解除しました。";
        if (ja == null) return null;

        return ($"告。{ja}", $"Notice. {result}");
    }

    /// <summary>The PC's own volume/mute: only the exact success messages system_volume produces.</summary>
    private static (string Ja, string En)? SystemVolumeReply(string result)
    {
        if (result == "System volume muted.")
            return Pick(
                ("告。音量をミュートにしました。", "Notice. System volume muted."),
                ("告。システム音量をミュートに設定しました。実行結果：成功。", "Notice. System volume has been muted. Execution result: success."));

        if (result == "System volume unmuted.")
            return Pick(
                ("告。ミュートを解除しました。", "Notice. System volume unmuted."),
                ("告。システム音量のミュートを解除しました。実行結果：成功。", "Notice. System volume mute has been lifted. Execution result: success."));

        var m = Regex.Match(result, @"^System volume set to (\d+)%\.$");
        return m.Success
            ? ($"告。音量を{m.Groups[1].Value}パーセントに設定しました。実行結果：成功。", $"Notice. {result} Execution result: success.")
            : null; // status and errors go through Gemini
    }
}
