using System.Globalization;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// Looks up the New York Times Wordle answer from the NYT's own public daily puzzle data
/// (https://www.nytimes.com/svc/wordle/v2/YYYY-MM-DD.json). The puzzle for a date is keyed by the calendar date, and
/// Wordle turns over at each player's local midnight, so "today" here is the date on this PC's clock.
/// </summary>
public static class WordleLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The puzzle for a day, or null if the NYT has not published one for that date.</summary>
    public static async Task<(int Number, string Date, string Solution)?> LookupAsync(DateTime day)
    {
        using var response = await Http.GetAsync($"https://www.nytimes.com/svc/wordle/v2/{day:yyyy-MM-dd}.json");
        if (!response.IsSuccessStatusCode) return null;

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var solution = json?["solution"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(solution)) return null;

        return (json!["days_since_launch"]?.GetValue<int>() ?? 0,
                json["print_date"]?.GetValue<string>() ?? $"{day:yyyy-MM-dd}",
                solution.Trim().ToUpperInvariant());
    }

    /// <param name="date">"yyyy-MM-dd", or empty for today (on this PC's clock).</param>
    public static async Task<string> AnswerAsync(string? date)
    {
        var day = DateTime.Now.Date;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                return $"'{date}' is not a date in the form yyyy-MM-dd.";
        }

        try
        {
            var puzzle = await LookupAsync(day);
            if (puzzle == null) return $"The New York Times has not published a Wordle for {day:yyyy-MM-dd} (or it could not be reached).";

            var (number, printDate, solution) = puzzle.Value;
            // Spelling it out makes it clear when spoken by a Japanese voice.
            return $"Wordle #{number} for {printDate}: the answer is {solution} (spelled {string.Join("-", solution.ToCharArray())}). "
                 + "In the Japanese line, say the letters one by one; in the English line, write the word in capitals.";
        }
        catch (Exception ex)
        {
            return $"Could not look up the Wordle answer: {ex.Message}";
        }
    }
}
