namespace Raphael;

/// <summary>
/// Watches your inbox for new mail and reports who it is from. Promotions, social, forum and update mail is skipped so
/// she doesn't announce every advert. Only the sender is ever fetched, never a subject or body.
/// </summary>
public sealed class GmailWatcher : IDisposable
{
    private const int LookAt = 25;   // how many of the newest unread messages to consider each time
    private static readonly string[] NoisyCategories =
        { "CATEGORY_PROMOTIONS", "CATEGORY_SOCIAL", "CATEGORY_FORUMS", "CATEGORY_UPDATES" };

    private readonly Func<int, Task<IReadOnlyList<string>>> _listUnread;
    private readonly Func<string, Task<MailInfo?>> _getMail;
    private readonly Func<IReadOnlyList<MailInfo>, bool, Task> _announce;
    private readonly Func<Task>? _onLoginRequired;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _seen = new();
    private bool _loginPromptedRecently;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, (MailInfo Info, DateTimeOffset Since)> _pending = new(); // mail that arrived while she ran and is still unread

    /// <param name="announce">Called with the new mail and whether this is the start-up summary of what was already unread.</param>
    /// <param name="onLoginRequired">Called (at most once per hour) when Google no longer accepts the saved sign-in.</param>
    public GmailWatcher(Func<int, Task<IReadOnlyList<string>>> listUnread, Func<string, Task<MailInfo?>> getMail,
        Func<IReadOnlyList<MailInfo>, bool, Task> announce, Func<Task>? onLoginRequired = null, TimeSpan? interval = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _listUnread = listUnread;
        _getMail = getMail;
        _announce = announce;
        _onLoginRequired = onLoginRequired;
        _interval = interval ?? TimeSpan.FromSeconds(45);
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ids = await _listUnread(LookAt);
                var fresh = ids.Where(id => !_seen.Contains(id)).ToList();
                foreach (var id in ids) _seen.Add(id);
                if (_seen.Count > 500) _seen.Clear(); // it only needs to remember the recent ones; an old id can't reappear at the top

                // Mail that has been read (or moved) since is no longer waiting.
                lock (_pendingLock)
                {
                    foreach (var gone in _pending.Keys.Where(k => !ids.Contains(k)).ToList()) _pending.Remove(gone);
                }

                if (fresh.Count > 0)
                {
                    var mails = await FetchRelevantAsync(fresh);
                    if (mails.Count > 0)
                    {
                        // Only mail that arrived while she was running counts as "left unread"; what was already
                        // there at start-up has no known arrival time.
                        if (!first) lock (_pendingLock) foreach (var m in mails) _pending[m.Id] = (m, _now());
                        await _announce(mails, first);
                    }
                }
                first = false;
            }
            catch (GmailLoginRequiredException)
            {
                if (_onLoginRequired != null && !_loginPromptedRecently)
                {
                    _loginPromptedRecently = true;
                    _ = Task.Delay(TimeSpan.FromHours(1)).ContinueWith(_ => _loginPromptedRecently = false);
                    try { await _onLoginRequired(); } catch { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A network hiccup or a busy Gmail: try again next round.
            }

            try { await Task.Delay(_interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<List<MailInfo>> FetchRelevantAsync(IEnumerable<string> ids)
    {
        var mails = new List<MailInfo>();
        foreach (var id in ids)
        {
            var mail = await _getMail(id);
            if (mail != null && !mail.Labels.Any(l => NoisyCategories.Contains(l))) mails.Add(mail);
        }
        return mails;
    }

    /// <summary>Mail that arrived while she was running and has stayed unread for at least this long, oldest first.</summary>
    public IReadOnlyList<(MailInfo Mail, TimeSpan Age)> Lingering(TimeSpan olderThan)
    {
        lock (_pendingLock)
        {
            var now = _now();
            return _pending.Values.Select(p => (p.Info, Age: now - p.Since))
                .Where(x => x.Age >= olderThan).OrderByDescending(x => x.Age)
                .Select(x => (x.Info, x.Age)).ToList();
        }
    }

    /// <summary>The unread mail in your inbox right now (senders only), for the check_mail tool.</summary>
    public async Task<string> DescribeAsync()
    {
        try
        {
            var ids = await _listUnread(LookAt);
            var mails = await FetchRelevantAsync(ids);
            if (mails.Count == 0) return "No unread mail in the main inbox.";

            var senders = mails.Select(m => m.Sender).Distinct().Take(8).ToList();
            var more = ids.Count >= LookAt ? " (and possibly more)" : "";
            return $"{mails.Count} unread in the main inbox{more}, from: {string.Join(", ", senders)}.";
        }
        catch (GmailLoginRequiredException)
        {
            return "Gmail needs to be signed in again. Restart Raphael and approve the Google login that opens.";
        }
        catch (Exception ex)
        {
            return $"Could not check Gmail: {ex.Message}";
        }
    }

    public void Dispose() => _cts.Cancel();
}
