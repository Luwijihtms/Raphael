namespace Raphael;

/// <summary>
/// A real calculator for anything beyond trivial arithmetic — math, unit conversions, equations, physics/chemistry
/// figures — through Wolfram Alpha's free "Short Answers" API, instead of Gemini guessing an answer from language
/// patterns (which is fine for "2+2" but not reliable for anything more involved). Needs a free personal AppID from
/// https://developer.wolframalpha.com (WOLFRAM_APP_ID); about 2,000 queries/month free.
/// </summary>
public static class WolframClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WOLFRAM_APP_ID"));

    public static async Task<string> ComputeAsync(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return "No question was given.";

        var appId = Environment.GetEnvironmentVariable("WOLFRAM_APP_ID")?.Trim();
        if (string.IsNullOrEmpty(appId))
            return "Wolfram Alpha is not set up (WOLFRAM_APP_ID is missing), so this can't be computed precisely.";

        var url = $"https://api.wolframalpha.com/v1/result?appid={Uri.EscapeDataString(appId)}&i={Uri.EscapeDataString(query)}&units=metric";
        try
        {
            using var response = await Http.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) return body.Trim();

            // A 501 with "Wolfram|Alpha did not understand..." is Wolfram's normal "no answer" response, not a fault.
            if ((int)response.StatusCode == 501)
                return $"Wolfram Alpha could not compute an answer for '{query}'. Rephrase it as a precise math/unit/science question, or answer from general knowledge instead if that's more appropriate.";

            return $"Wolfram Alpha could not be reached ({(int)response.StatusCode}). Answer from general knowledge instead, but say the figure is an estimate, not a computed one.";
        }
        catch (Exception ex)
        {
            return $"Wolfram Alpha request failed: {ex.Message}. Answer from general knowledge instead, but say the figure is an estimate, not a computed one.";
        }
    }
}
