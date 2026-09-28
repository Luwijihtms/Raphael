using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>Google no longer accepts the saved login (it expired, or was revoked): a new sign-in is needed.</summary>
public sealed class GmailLoginRequiredException : Exception
{
    public GmailLoginRequiredException(string message) : base(message) { }
}

/// <summary>
/// One Google sign-in shared by Gmail and Calendar. It asks for the two smallest read-only permissions: "gmail.metadata"
/// (message headers and labels, never the text of any message) and "calendar.readonly". Signing in uses OAuth with PKCE and
/// a loopback address, the same way the Spotify login works. Needs GMAIL_CLIENT_ID and GMAIL_CLIENT_SECRET (a "Desktop app"
/// OAuth client from your own Google Cloud project; for desktop apps Google treats the secret as public).
/// </summary>
public sealed class GoogleAuth
{
    // calendar.events (not the broader "calendar" scope) lets her create/read events without touching calendar settings
    // or other calendars. This is a superset of the old read-only scope, so any previously saved sign-in is no longer
    // enough and one more Google approval is needed after this changes.
    public const string Scopes =
        "https://www.googleapis.com/auth/gmail.metadata https://www.googleapis.com/auth/calendar.events";

    private readonly HttpClient _http;
    private readonly Action<string> _openBrowser;
    private readonly string _tokenPath;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _expiresAt = DateTime.MinValue;

    public GoogleAuth(HttpClient? http = null, Action<string>? openBrowser = null, string? tokenPath = null,
        string? clientId = null, string? clientSecret = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _openBrowser = openBrowser ?? (url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }));
        _tokenPath = tokenPath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "gmail_token.json");
        _clientId = (clientId ?? Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID"))?.Trim();
        _clientSecret = (clientSecret ?? Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET"))?.Trim();
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    /// <summary>True once a sign-in that covers everything she needs has been completed and saved.</summary>
    public bool HasLogin
    {
        get
        {
            LoadStoredToken();
            return _refreshToken != null;
        }
    }

    /// <summary>An authorised GET that returns the parsed JSON. Failures are an HttpRequestException carrying the status code.</summary>
    public Task<JsonNode> GetJsonAsync(string url) => SendJsonAsync(HttpMethod.Get, url, null);

    /// <summary>An authorised POST with a JSON body, returning the parsed JSON response.</summary>
    public Task<JsonNode> PostJsonAsync(string url, JsonObject body) => SendJsonAsync(HttpMethod.Post, url, body);

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string url, JsonObject? body)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await EnsureTokenAsync();
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _accessToken = null; // expired early: refresh and try once more
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}", null, response.StatusCode);

            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
    }

    // ---- signing in (OAuth 2.0, PKCE, loopback) -----------------------------------------------------------

    public static string BuildAuthUrl(string clientId, string redirectUri, string challenge, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth"
        + $"?client_id={Uri.EscapeDataString(clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + "&response_type=code"
        + $"&scope={Uri.EscapeDataString(Scopes)}"
        + "&code_challenge_method=S256"
        + $"&code_challenge={challenge}"
        + $"&state={state}"
        + "&access_type=offline"      // so Google gives us a refresh token
        + "&prompt=consent";          // ...every time, even if you signed in before

    /// <summary>Opens Google's sign-in page in the browser and waits (up to 3 minutes) for you to approve access.</summary>
    public async Task LoginAsync()
    {
        if (!IsConfigured) throw new InvalidOperationException("GMAIL_CLIENT_ID and GMAIL_CLIENT_SECRET are not set.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // Google lets a desktop app use http://127.0.0.1 on any free port.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var redirect = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        _openBrowser(BuildAuthUrl(_clientId!, redirect, challenge, state));

        var contextTask = listener.GetContextAsync();
        if (await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3))) != contextTask)
            throw new TimeoutException("The Google sign-in was not completed within 3 minutes.");

        var context = await contextTask;
        var query = context.Request.QueryString;
        var code = query["code"];
        var error = query["error"];
        var ok = code != null && query["state"] == state;

        var page = Encoding.UTF8.GetBytes(ok
            ? "<html><body style='font-family:sans-serif'><h2>Raphael is connected to your Google account.</h2>You can close this tab.</body></html>"
            : $"<html><body style='font-family:sans-serif'><h2>Google sign-in failed.</h2>{WebUtility.HtmlEncode(error ?? "state mismatch")}</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();

        if (!ok) throw new InvalidOperationException($"Google sign-in failed: {error ?? "state mismatch"}");

        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirect,
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["code_verifier"] = verifier
        });
    }

    private async Task<string> EnsureTokenAsync()
    {
        await _tokenLock.WaitAsync(); // Gmail and Calendar both call this; only one refresh at a time
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _expiresAt - TimeSpan.FromSeconds(60)) return _accessToken;

            LoadStoredToken();
            if (_refreshToken == null) throw new GmailLoginRequiredException("Google is not signed in yet.");

            await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _clientId!,
                ["client_secret"] = _clientSecret!
            });
            return _accessToken!;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task RequestTokenAsync(Dictionary<string, string> form)
    {
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // "invalid_grant": the saved login has expired (a test-mode app's expires after 7 days) or was revoked.
            if (raw.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                _accessToken = null;
                _refreshToken = null;
                try { File.Delete(_tokenPath); } catch { }
                throw new GmailLoginRequiredException("Google no longer accepts the saved sign-in.");
            }
            throw new HttpRequestException($"Google's token service answered {(int)response.StatusCode}: {raw}", null, response.StatusCode);
        }

        var json = JsonNode.Parse(raw)!;
        _accessToken = json["access_token"]!.GetValue<string>();
        _expiresAt = DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600);
        _refreshToken = json["refresh_token"]?.GetValue<string>() ?? _refreshToken; // a refresh response usually omits it
        SaveStoredToken();
    }

    private void LoadStoredToken()
    {
        if (_refreshToken != null) return;
        try
        {
            if (!File.Exists(_tokenPath)) return;
            var saved = JsonNode.Parse(File.ReadAllText(_tokenPath));
            // A login granted under different permissions can't be reused: she signs in again to get both.
            if (saved?["scope"]?.GetValue<string>() == Scopes) _refreshToken = saved["refresh_token"]?.GetValue<string>();
        }
        catch { /* a damaged file just means "not signed in" */ }
    }

    private void SaveStoredToken()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_tokenPath)!);
            File.WriteAllText(_tokenPath, new JsonObject { ["refresh_token"] = _refreshToken, ["scope"] = Scopes }.ToJsonString());
        }
        catch { /* worst case you sign in again next time */ }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
