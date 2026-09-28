using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// Finds a video with the YouTube Data API and opens it in the default browser.
/// Needs YOUTUBE_API_KEY (a free key restricted to "YouTube Data API v3").
/// </summary>
public class YouTubeClient
{
    private const int ResultCount = 15;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY")?.Trim();

    /// <param name="random">True: any of the top results at random. False: the best match.</param>
    public async Task<string> PlayAsync(string query, bool random)
    {
        if (string.IsNullOrEmpty(_apiKey))
            return "YouTube search is not configured. The YOUTUBE_API_KEY environment variable is not set.";
        if (string.IsNullOrWhiteSpace(query))
            return "No search was given.";

        try
        {
            var url = "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&safeSearch=moderate"
                    + $"&maxResults={ResultCount}&q={Uri.EscapeDataString(query)}&key={_apiKey}";

            using var response = await _http.GetAsync(url);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            if (!response.IsSuccessStatusCode)
            {
                var reason = json["error"]?["errors"]?[0]?["reason"]?.GetValue<string>();
                return reason == "quotaExceeded"
                    ? "YouTube's daily search limit has been reached. Try again tomorrow."
                    : $"YouTube search failed ({(int)response.StatusCode}): {json["error"]?["message"]?.GetValue<string>()}";
            }

            var videos = json["items"]?.AsArray().Where(i => i?["id"]?["videoId"] != null).ToList();
            if (videos == null || videos.Count == 0)
                return $"No YouTube videos were found for '{query}'.";

            var pick = random ? videos[Random.Shared.Next(videos.Count)]! : videos[0]!;
            var id = pick["id"]!["videoId"]!.GetValue<string>();
            var title = WebUtility.HtmlDecode(pick["snippet"]?["title"]?.GetValue<string>() ?? "a video");
            var channel = WebUtility.HtmlDecode(pick["snippet"]?["channelTitle"]?.GetValue<string>() ?? "unknown channel");

            Process.Start(new ProcessStartInfo($"https://www.youtube.com/watch?v={id}") { UseShellExecute = true });

            // Browsers often refuse to autoplay: give the page a few seconds to load, then press play ourselves.
            var playing = await MediaControl.StartBrowserPlaybackAsync(title, TimeSpan.FromSeconds(10));
            return $"Opened the YouTube video '{title}' by {channel} in the browser"
                 + (playing ? " and it is now playing."
                            : ", but it could not be started automatically; the page may need a click.");
        }
        catch (Exception ex)
        {
            // The request URL contains the key, so keep it out of anything that gets printed or logged.
            return $"YouTube request failed: {ex.Message.Replace(_apiKey, "<key>")}";
        }
    }
}
