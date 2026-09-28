using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Raphael;

/// <summary>
/// A free, key-less way for her to look things up instead of answering only from what Gemini memorised: recent headlines
/// from Google News' public RSS feed, and short reference summaries from Wikipedia. Neither is a full web search — only
/// headlines and article summaries come back, never page contents — and the news feed is unofficial and could change.
/// What you ask about is sent to those two services, like any search would be.
/// </summary>
public static class WebLookup
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // Wikimedia asks API users to identify themselves; a plain descriptive agent is also friendlier to the news feed.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RaphaelDesktopAssistant/1.0 (personal, non-commercial)");
        return http;
    }

    /// <param name="type">"news", "reference" or anything else for both.</param>
    public static async Task<string> LookupAsync(string query, string? type)
    {
        query = (query ?? "").Trim();
        if (query.Length < 2) return "No search terms were given.";

        var kind = (type ?? "").Trim().ToLowerInvariant();
        var wantNews = kind != "reference";
        var wantReference = kind != "news";

        var newsTask = wantNews ? SafeAsync(() => NewsAsync(query)) : Task.FromResult<(string?, bool)>((null, false));
        var refTask = wantReference ? SafeAsync(() => ReferenceAsync(query)) : Task.FromResult<(string?, bool)>((null, false));
        await Task.WhenAll(newsTask, refTask);

        var (news, newsFailed) = await newsTask;
        var (reference, refFailed) = await refTask;

        if (news == null && reference == null)
        {
            // "The service failed" and "the service found nothing" mean different things and must not be blurred together.
            if (newsFailed || refFailed)
                return $"The lookup for '{query}' could not be completed: the news/reference service didn't answer (it may be busy or offline). Try again in a moment. This says nothing about whether the claim is true.";
            return $"Nothing was found for '{query}' in recent news or Wikipedia. That does NOT mean it is false or didn't happen — only that this lookup didn't find it, so it can't be confirmed or denied.";
        }

        var sb = new StringBuilder($"Lookup for '{query}' (headlines and short summaries only, not full articles; unverified):");
        if (news != null) sb.Append("\nRecent news: ").Append(news);
        if (reference != null) sb.Append("\nWikipedia: ").Append(reference);
        if (newsFailed || refFailed) sb.Append("\n(One of the two sources didn't answer, so this may be incomplete.)");
        return sb.ToString();
    }

    /// <summary>Runs one source, telling "it answered with nothing" (null, false) apart from "it failed" (null, true).</summary>
    private static async Task<(string? Text, bool Failed)> SafeAsync(Func<Task<string?>> work)
    {
        try { return (await work(), false); }
        catch { return (null, true); }
    }

    /// <summary>The top few recent headlines, with their source and date.</summary>
    private static async Task<string?> NewsAsync(string query)
    {
        // Google News' feed quirk: an all-lowercase query ("philippines ban discord") comes back empty while the same words
        // capitalised ("Philippines Ban Discord") return dozens of stories, and Gemini writes lowercase keyword queries.
        // So ask with the words capitalised first, and only fall back to the query as given if that finds nothing.
        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(query);
        var elements = await NewsItemsAsync(titled);
        if (elements.Count == 0 && titled != query) elements = await NewsItemsAsync(query);

        var items = elements.Take(5).Select(item =>
        {
            var source = (string?)item.Element("source");
            var title = ((string?)item.Element("title") ?? "").Trim();
            // Titles arrive as "Headline - Source"; the source is shown separately, so drop the duplicate suffix.
            if (source != null && title.EndsWith(" - " + source, StringComparison.Ordinal)) title = title[..^(source.Length + 3)];
            var when = DateTimeOffset.TryParse((string?)item.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture) : null;
            return title.Length == 0 ? null : $"\"{title}\" ({string.Join(", ", new[] { source, when }.Where(x => x != null))})";
        }).Where(s => s != null).ToList();

        return items.Count == 0 ? null : string.Join("; ", items);
    }

    private static async Task<List<XElement>> NewsItemsAsync(string query)
    {
        var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl=en-US&gl=US&ceid=US:en";
        var xml = await Http.GetStringAsync(url);
        return XDocument.Parse(xml).Descendants("item").ToList();
    }

    /// <summary>The intro of the best-matching Wikipedia article, trimmed to a couple of sentences.</summary>
    private static async Task<string?> ReferenceAsync(string query)
    {
        var searchUrl = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=5&srsearch="
            + Uri.EscapeDataString(query);
        var search = JsonNode.Parse(await Http.GetStringAsync(searchUrl));
        var titles = search?["query"]?["search"]?.AsArray().Select(r => r?["title"]?.GetValue<string>()).Where(t => t != null).Select(t => t!).ToList()
                     ?? new List<string>();
        if (titles.Count == 0) return null;

        // Full-text ranking can put a side article first ("Eiffel Tower height" -> the Paris, Texas replica). Prefer an
        // article whose plain title is what the query is about (contained in it, no "(disambiguation)" suffix); otherwise
        // trust the search's first hit.
        var title = titles.FirstOrDefault(t => !t.Contains('(') && query.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? titles[0];

        var summaryUrl = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(title.Replace(' ', '_'));
        var summary = JsonNode.Parse(await Http.GetStringAsync(summaryUrl));
        var extract = summary?["extract"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(extract)) return null;

        if (extract.Length > 500)
        {
            var cut = extract.LastIndexOf(". ", 500, StringComparison.Ordinal);
            extract = cut > 150 ? extract[..(cut + 1)] : extract[..500] + "…";
        }
        return $"{title} — {extract}";
    }
}
