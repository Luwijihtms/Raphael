namespace Raphael;

/// <summary>
/// <c>Raphael.exe --ui-preview</c>: opens only the window, with a few sample lines and a speaking orb, so the layout can be
/// looked at without Gemini, VOICEVOX, Telegram or another running copy of her. Typed lines are simply echoed back.
/// </summary>
public static class UiPreview
{
    public static async Task RunAsync()
    {
        var orb = new OrbOverlay();
        ChatWindow.Start(orb, startMinimized: false);

        ChatWindow.AddReply("告。ラファエルです。システム正常。ご用件をどうぞ。", "Notice. I am Raphael. Systems normal. Your instructions, please.");
        ChatWindow.AddUser("what is slowing my pc down?", "voice");
        ChatWindow.AddReply("告。Chromeが全体のCPUの四十パーセントを使用しています。終了しますか？", "Notice. Chrome is using 40% of the CPU. Shall I end it?");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2500);
                orb.SetThinking(true);
                await Task.Delay(2000);
                orb.SetThinking(false);
                orb.SpeakSynthetic(TimeSpan.FromSeconds(5));
                await Task.Delay(9000);
            }
        });

        while (true)
        {
            var line = await ChatWindow.ReadLineAsync();
            if (line == null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            ChatWindow.AddReply("告。了解しました。", $"Notice. Understood: {line}");
        }
        Environment.Exit(0);
    }
}
