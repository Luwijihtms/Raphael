using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// Searches Spotify and starts playback through the Web API (Premium required).
/// Uses the Authorization Code + PKCE flow, so no client secret is needed — only the
/// Client ID of a (free) app created at https://developer.spotify.com/dashboard.
/// The refresh token is stored under %LOCALAPPDATA%\Raphael, outside the project.
/// </summary>
public class SpotifyClient
{
    // Must match a Redirect URI registered on the Spotify app exactly.
    private const string RedirectUri = "http://127.0.0.1:8888/callback";
    private const string ListenerPrefix = "http://127.0.0.1:8888/";
    private const string Scopes =
        "user-modify-playback-state user-read-playback-state playlist-read-private playlist-read-collaborative";

    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "spotify_token.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string? _clientId = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID")?.Trim();

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _expiresAt = DateTime.MinValue;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

    public async Task<string> PlayAsync(string query)
    {
        if (!IsConfigured)
            return "Spotify is not configured. The SPOTIFY_CLIENT_ID environment variable is not set.";
        if (string.IsNullOrWhiteSpace(query))
            return "No song specified.";

        try
        {
            await EnsureTokenAsync();

            // 1. Find the track.
            var search = await ApiAsync(HttpMethod.Get,
                $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1");
            if (!search.IsSuccessStatusCode)
                return $"Spotify search failed ({(int)search.StatusCode}): {await search.Content.ReadAsStringAsync()}";

            var track = JsonNode.Parse(await search.Content.ReadAsStringAsync())?["tracks"]?["items"]?[0];
            if (track == null) return $"No Spotify track found for '{query}'.";

            var uri = track["uri"]!.GetValue<string>();
            var title = track["name"]!.GetValue<string>();
            var artist = track["artists"]?[0]?["name"]?.GetValue<string>() ?? "unknown artist";

            var error = await StartPlaybackAsync(new JsonObject { ["uris"] = new JsonArray { uri } });
            return error ?? $"Now playing the single track '{title}' by {artist}.";
        }
        catch (Exception ex)
        {
            return $"Spotify request failed: {ex.Message}";
        }
    }

    /// <summary>Pause, resume, skip, volume, shuffle, repeat, or report what is playing.</summary>
    public async Task<string> ControlAsync(string action, int? value)
    {
        if (!IsConfigured)
            return "Spotify is not configured. The SPOTIFY_CLIENT_ID environment variable is not set.";

        try
        {
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "now_playing": return await NowPlayingAsync();
                case "pause": return await PlayerCommandAsync(HttpMethod.Put, "pause", "Paused.");
                case "resume": return await PlayerCommandAsync(HttpMethod.Put, "play", "Resumed playback.");
                case "next": return await PlayerCommandAsync(HttpMethod.Post, "next", "Skipped to the next track.");
                case "previous": return await PlayerCommandAsync(HttpMethod.Post, "previous", "Went back to the previous track.");
                case "shuffle_on": return await PlayerCommandAsync(HttpMethod.Put, "shuffle?state=true", "Shuffle is on.");
                case "shuffle_off": return await PlayerCommandAsync(HttpMethod.Put, "shuffle?state=false", "Shuffle is off.");
                case "repeat_track": return await PlayerCommandAsync(HttpMethod.Put, "repeat?state=track", "Repeating the current track.");
                case "repeat_playlist": return await PlayerCommandAsync(HttpMethod.Put, "repeat?state=context", "Repeating the current playlist.");
                case "repeat_off": return await PlayerCommandAsync(HttpMethod.Put, "repeat?state=off", "Repeat is off.");

                case "volume":
                    return value == null ? "No volume level was given." : await SetVolumeAsync(value.Value);

                case "volume_up":
                case "volume_down":
                {
                    var current = await GetVolumeAsync();
                    if (current == null) return "Could not read the current Spotify volume. Start playing something first.";
                    var step = value ?? 10;
                    return await SetVolumeAsync(action!.Trim().ToLowerInvariant() == "volume_up" ? current.Value + step : current.Value - step);
                }

                default: return $"Unknown Spotify action '{action}'.";
            }
        }
        catch (Exception ex)
        {
            return $"Spotify request failed: {ex.Message}";
        }
    }

    private Task<string> SetVolumeAsync(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return PlayerCommandAsync(HttpMethod.Put, $"volume?volume_percent={percent}", $"Spotify volume set to {percent} percent.");
    }

    /// <summary>Sends a player command to the active device; retries against any available device if none is active.</summary>
    private async Task<string> PlayerCommandAsync(HttpMethod method, string path, string okMessage)
    {
        var url = $"https://api.spotify.com/v1/me/player/{path}";
        var resp = await ApiAsync(method, url);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var deviceId = await FindAnyDeviceAsync();
            if (deviceId != null)
                resp = await ApiAsync(method, $"{url}{(path.Contains('?') ? "&" : "?")}device_id={Uri.EscapeDataString(deviceId)}");
        }

        if (resp.IsSuccessStatusCode) return okMessage;
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return "No active Spotify device. Start playing something on Spotify first.";
        return $"Spotify refused the request ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}";
    }

    private async Task<string?> FindAnyDeviceAsync()
    {
        var resp = await ApiAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player/devices");
        if (!resp.IsSuccessStatusCode) return null;

        var devices = JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["devices"]?.AsArray();
        var usable = devices?.Where(d => d?["is_restricted"]?.GetValue<bool>() != true).ToList();
        return (usable?.FirstOrDefault(d => d?["is_active"]?.GetValue<bool>() == true) ?? usable?.FirstOrDefault())?["id"]?.GetValue<string>();
    }

    private async Task<int?> GetVolumeAsync()
    {
        var state = await GetPlayerStateAsync();
        return state?["device"]?["volume_percent"]?.GetValue<int>();
    }

    /// <summary>The player state, or null when nothing is playing (Spotify answers 204).</summary>
    private async Task<JsonNode?> GetPlayerStateAsync()
    {
        var resp = await ApiAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player");
        if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
        var raw = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
    }

    private async Task<string> NowPlayingAsync()
    {
        var state = await GetPlayerStateAsync();
        var item = state?["item"];
        if (item == null) return "Nothing is playing on Spotify right now.";

        var title = item["name"]?.GetValue<string>() ?? "unknown";
        var artist = item["artists"]?[0]?["name"]?.GetValue<string>();
        var playing = state!["is_playing"]?.GetValue<bool>() == true ? "Playing" : "Paused on";
        var volume = state["device"]?["volume_percent"]?.GetValue<int>();

        return $"{playing} '{title}'{(artist != null ? $" by {artist}" : "")}"
             + (volume != null ? $". Volume is {volume} percent." : ".");
    }

    /// <summary>Plays one of the user's own (or followed) playlists, matched by name.</summary>
    public async Task<string> PlayPlaylistAsync(string name)
    {
        if (!IsConfigured)
            return "Spotify is not configured. The SPOTIFY_CLIENT_ID environment variable is not set.";
        if (string.IsNullOrWhiteSpace(name))
            return "No playlist name specified.";

        try
        {
            var playlists = await GetPlaylistsAsync();
            var q = name.Trim().ToLowerInvariant();

            // Exact name, else shortest name containing the query, else a query containing the name.
            var match = playlists.FirstOrDefault(p => p.Name.ToLowerInvariant() == q);
            if (match.Name == null)
                match = playlists.Where(p => p.Name.ToLowerInvariant().Contains(q)).OrderBy(p => p.Name.Length).FirstOrDefault();
            if (match.Name == null)
                match = playlists.Where(p => p.Name.Length > 0 && q.Contains(p.Name.ToLowerInvariant()))
                                 .OrderByDescending(p => p.Name.Length).FirstOrDefault();

            if (match.Name == null)
                return $"None of the user's playlists matches '{name}'. Their playlists are: {FormatNames(playlists)}.";

            var error = await StartPlaybackAsync(new JsonObject { ["context_uri"] = match.Uri });
            return error ?? $"Now playing the playlist '{match.Name}' ({match.Total} tracks).";
        }
        catch (Exception ex)
        {
            return $"Spotify request failed: {ex.Message}";
        }
    }

    public async Task<string> ListPlaylistsAsync()
    {
        if (!IsConfigured)
            return "Spotify is not configured. The SPOTIFY_CLIENT_ID environment variable is not set.";
        try
        {
            var playlists = await GetPlaylistsAsync();
            return playlists.Count == 0 ? "The user has no playlists." : $"The user's playlists: {FormatNames(playlists)}.";
        }
        catch (Exception ex)
        {
            return $"Spotify request failed: {ex.Message}";
        }
    }

    private static string FormatNames(List<(string Name, string Uri, int Total)> playlists) =>
        string.Join(", ", playlists.Take(30).Select(p => $"'{p.Name}'"));

    private async Task<List<(string Name, string Uri, int Total)>> GetPlaylistsAsync()
    {
        var result = new List<(string, string, int)>();
        string? url = "https://api.spotify.com/v1/me/playlists?limit=50";

        for (var page = 0; url != null && page < 4; page++) // up to 200 playlists
        {
            var resp = await ApiAsync(HttpMethod.Get, url);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Could not read playlists ({(int)resp.StatusCode}): {raw}");

            var json = JsonNode.Parse(raw)!;
            foreach (var item in json["items"]!.AsArray())
            {
                if (item == null) continue; // Spotify can return null entries for unavailable playlists
                var total = item["items"]?["total"]?.GetValue<int>() ?? item["tracks"]?["total"]?.GetValue<int>() ?? 0;
                result.Add((item["name"]?.GetValue<string>() ?? "", item["uri"]!.GetValue<string>(), total));
            }
            url = json["next"]?.GetValue<string>();
        }
        return result;
    }

    /// <summary>Starts playback with the given body on a suitable device. Returns null on success, else an error message.</summary>
    private async Task<string?> StartPlaybackAsync(JsonObject body)
    {
        var deviceId = await GetDeviceIdAsync();
        if (deviceId == null)
            return "No Spotify device is available. Open Spotify on this PC and try again.";

        var play = await ApiAsync(HttpMethod.Put,
            $"https://api.spotify.com/v1/me/player/play?device_id={Uri.EscapeDataString(deviceId)}", body);
        return play.IsSuccessStatusCode
            ? null
            : $"Spotify refused to start playback ({(int)play.StatusCode}): {await play.Content.ReadAsStringAsync()}";
    }

    /// <summary>Picks the active device, else the first one; launches Spotify and waits if there are none.</summary>
    private async Task<string?> GetDeviceIdAsync()
    {
        var launched = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var resp = await ApiAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player/devices");
            if (resp.IsSuccessStatusCode)
            {
                var devices = JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["devices"]?.AsArray();
                var usable = devices?.Where(d => d?["is_restricted"]?.GetValue<bool>() != true).ToList();
                var chosen = usable?.FirstOrDefault(d => d?["is_active"]?.GetValue<bool>() == true) ?? usable?.FirstOrDefault();
                if (chosen != null) return chosen["id"]?.GetValue<string>();
            }

            if (!launched)
            {
                launched = true;
                Console.WriteLine("No Spotify device found — launching Spotify...");
                CommandHandler.OpenApp("spotify");
            }
            await Task.Delay(1000);
        }
        return null;
    }

    // ---- HTTP helper ------------------------------------------------------------------

    private async Task<HttpResponseMessage> ApiAsync(HttpMethod method, string url, JsonNode? body = null)
    {
        await EnsureTokenAsync();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        else if (method != HttpMethod.Get)
            request.Content = new StringContent(""); // Spotify answers 411 to a PUT/POST without Content-Length
        return await _http.SendAsync(request);
    }

    // ---- Auth (PKCE) ------------------------------------------------------------------

    private async Task EnsureTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _expiresAt - TimeSpan.FromSeconds(60)) return;

        if (_refreshToken == null) LoadStoredToken();

        if (_refreshToken != null)
        {
            try
            {
                await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _refreshToken,
                    ["client_id"] = _clientId!
                });
                return;
            }
            catch
            {
                // Refresh token revoked or expired: fall back to a fresh login.
                _refreshToken = null;
            }
        }

        await LoginInBrowserAsync();
    }

    private async Task LoginInBrowserAsync()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authUrl = "https://accounts.spotify.com/authorize"
            + $"?client_id={Uri.EscapeDataString(_clientId!)}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&code_challenge_method=S256"
            + $"&code_challenge={challenge}"
            + $"&scope={Uri.EscapeDataString(Scopes)}"
            + $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenerPrefix);
        listener.Start();

        Console.WriteLine("Opening the Spotify login page in your browser (one-time setup)...");
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        if (await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3))) != contextTask)
            throw new TimeoutException("Spotify login was not completed within 3 minutes.");

        var context = await contextTask;
        var query = context.Request.QueryString;
        var code = query["code"];
        var error = query["error"];
        var ok = code != null && query["state"] == state;

        var page = Encoding.UTF8.GetBytes(ok
            ? "<html><body style='font-family:sans-serif'><h2>Raphael is connected to Spotify.</h2>You can close this tab.</body></html>"
            : $"<html><body style='font-family:sans-serif'><h2>Spotify login failed.</h2>{WebUtility.HtmlEncode(error ?? "state mismatch")}</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();

        if (!ok) throw new InvalidOperationException($"Spotify login failed: {error ?? "state mismatch"}");

        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = _clientId!,
            ["code_verifier"] = verifier
        });
    }

    private async Task RequestTokenAsync(Dictionary<string, string> form)
    {
        var resp = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Token request failed ({(int)resp.StatusCode}): {raw}");

        var json = JsonNode.Parse(raw)!;
        _accessToken = json["access_token"]!.GetValue<string>();
        _expiresAt = DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600);
        // A refresh response may omit refresh_token; keep the existing one in that case.
        _refreshToken = json["refresh_token"]?.GetValue<string>() ?? _refreshToken;
        SaveStoredToken();
    }

    private void LoadStoredToken()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var saved = JsonNode.Parse(File.ReadAllText(TokenPath));
                // A token granted under different scopes can't be upgraded silently: force a new login.
                if (saved?["scope"]?.GetValue<string>() == Scopes)
                    _refreshToken = saved["refresh_token"]?.GetValue<string>();
            }
        }
        catch { /* treat a corrupt file as "not logged in" */ }
    }

    private void SaveStoredToken()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            File.WriteAllText(TokenPath, new JsonObject { ["refresh_token"] = _refreshToken, ["scope"] = Scopes }.ToJsonString());
        }
        catch { /* worst case we log in again next run */ }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
