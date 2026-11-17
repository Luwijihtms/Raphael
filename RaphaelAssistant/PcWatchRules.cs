using System.Runtime.InteropServices;

namespace Raphael;

/// <summary>
/// More things she may point out on her own (they share the daily limit and gaps of <see cref="ProactiveWatcher"/>, so she stays
/// rare). All read-only: a program hogging the CPU, memory nearly full, and a PC that has not been restarted for a long time.
/// </summary>
public static class PcWatchRules
{
    private static readonly IReadOnlyList<Suggestion> None = Array.Empty<Suggestion>();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary>One program is using over 60% of the whole CPU on two checks in a row (about five minutes).</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> CpuHogRule()
    {
        string? previous = null;
        return async () =>
        {
            var top = await Task.Run(PcManager.TopCpu);
            if (top is not { Pct: >= 60 } t) { previous = null; return None; }

            var repeat = string.Equals(previous, t.Name, StringComparison.OrdinalIgnoreCase);
            previous = t.Name;
            if (!repeat) return None;

            var pct = (int)Math.Round(t.Pct);
            return new[]
            {
                new Suggestion($"cpuhog:{t.Name.ToLowerInvariant()}",
                    $"告。{t.Name}がCPUの{pct}パーセントを使用し続けています。動作が重い原因の可能性があります。",
                    $"Notice. {t.Name} has been using about {pct}% of the CPU for several minutes. It may be what is slowing the PC. Shall I look into it?",
                    TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), Category: "cpu")
            };
        };
    }

    /// <summary>Memory is over 92% full on two checks in a row.</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> MemoryRule()
    {
        var highBefore = false;
        return async () =>
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref status) || status.MemoryLoad < 92) { highBefore = false; return None; }

            var repeat = highBefore;
            highBefore = true;
            if (!repeat) return None;

            var top = await Task.Run(PcManager.TopMemory);
            var who = top is { } m ? $" {m.Name} is holding the most ({m.Bytes / 1073741824.0:0.0} GB)." : "";
            return new[]
            {
                new Suggestion("memory",
                    $"告。メモリ使用率が{status.MemoryLoad}パーセントに達しています。動作が重くなる可能性があります。",
                    $"Notice. Memory is {status.MemoryLoad}% full, which can slow everything down.{who}",
                    TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), Category: "memory")
            };
        };
    }

    /// <summary>The PC has run for more than 10 days since its last full boot; said at most once a week.</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> UptimeRule() => () =>
    {
        var days = (int)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalDays;
        if (days < 10) return Task.FromResult(None);
        return Task.FromResult<IReadOnlyList<Suggestion>>(new[]
        {
            new Suggestion("uptime",
                $"告。再起動せずに{days}日が経過しています。再起動を推奨します。",
                $"Notice. The PC has gone {days} days without a restart. A restart is recommended.",
                TimeSpan.FromDays(7), TimeSpan.FromHours(1), Category: "uptime")
        });
    };
}
