using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>A message in the inbox: who it is from and its labels. Never the subject or the body.</summary>
public sealed record MailInfo(string Id, string Sender, IReadOnlyList<string> Labels);

/// <summary>
/// Reads the sender of new mail through the Gmail API, with the smallest permission Google offers for this
/// ("gmail.metadata": message headers and labels, never the text of any message). The sign-in itself is <see cref="GoogleAuth"/>.
/// </summary>
public sealed class GmailClient
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";

    private readonly GoogleAuth _auth;

    public GmailClient(HttpClient? http = null, Action<string>? openBrowser = null, string? tokenPath = null,
        string? clientId = null, string? clientSecret = null, GoogleAuth? auth = null)
    {
        _auth = auth ?? new GoogleAuth(http, openBrowser, tokenPath, clientId, clientSecret);
    }

    public bool IsConfigured => _auth.IsConfigured;
    public bool HasLogin => _auth.HasLogin;
    public Task LoginAsync() => _auth.LoginAsync();

    // ---- reading mail ------------------------------------------------------------------------------------

    /// <summary>Ids of the newest unread messages in the inbox (newest first).</summary>
    public async Task<IReadOnlyList<string>> ListUnreadInboxIdsAsync(int max)
    {
        var json = await _auth.GetJsonAsync($"{Api}messages?labelIds=INBOX&labelIds=UNREAD&maxResults={max}");
        return json["messages"]?.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToList() ?? new List<string>();
    }

    /// <summary>The sender and labels of one message, or null if it has gone.</summary>
    public async Task<MailInfo?> GetMailAsync(string id)
    {
        JsonNode json;
        try { json = await _auth.GetJsonAsync($"{Api}messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From"); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }

        var from = json["payload"]?["headers"]?.AsArray()
            .FirstOrDefault(h => string.Equals(h?["name"]?.GetValue<string>(), "From", StringComparison.OrdinalIgnoreCase))?["value"]?.GetValue<string>() ?? "";
        var labels = json["labelIds"]?.AsArray().Select(l => l!.GetValue<string>()).ToList() ?? new List<string>();
        return new MailInfo(id, ParseSender(from), labels);
    }

    /// <summary>"Alex Smith" from '"Alex Smith" &lt;alex@example.com&gt;'; handles encoded (e.g. Japanese) names; falls back to the address.</summary>
    public static string ParseSender(string from)
    {
        from = DecodeEncodedWords(from.Trim());
        if (from.Length == 0) return "an unknown sender";

        var lt = from.LastIndexOf('<');
        var name = (lt > 0 ? from[..lt] : "").Replace("\"", "").Trim();
        if (name.Length > 0) return name;

        var address = lt >= 0 ? from[(lt + 1)..].TrimEnd('>', ' ') : from;
        var at = address.IndexOf('@');
        return at > 0 ? address[..at] : address.Length > 0 ? address : "an unknown sender";
    }

    /// <summary>Turns "=?UTF-8?B?...?=" / "=?UTF-8?Q?...?=" header words (how non-English names are sent) into plain text.</summary>
    private static string DecodeEncodedWords(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        {
            try
            {
                var encoding = Encoding.GetEncoding(m.Groups[1].Value);
                if (m.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase))
                    return encoding.GetString(Convert.FromBase64String(m.Groups[3].Value));

                var q = m.Groups[3].Value.Replace('_', ' ');
                var bytes = new List<byte>();
                for (var i = 0; i < q.Length; i++)
                {
                    if (q[i] == '=' && i + 2 < q.Length + 0 && Uri.IsHexDigit(q[i + 1]) && Uri.IsHexDigit(q[i + 2]))
                    {
                        bytes.Add(Convert.ToByte(q.Substring(i + 1, 2), 16));
                        i += 2;
                    }
                    else bytes.AddRange(Encoding.UTF8.GetBytes(q[i].ToString()));
                }
                return encoding.GetString(bytes.ToArray());
            }
            catch { return m.Value; }
        });
}
