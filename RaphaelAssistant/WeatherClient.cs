using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// The weather, from Open-Meteo (free, no API key). Your location is found once, either from
/// <c>RAPHAEL_WEATHER_LOCATION</c> (a city name, geocoded) or, if that isn't set, from your public IP address (roughly
/// city-level, no account or key needed either); it is then cached so this only happens once.
/// </summary>
public sealed class WeatherClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "weather_location.json");

    // World Meteorological Organization codes, as used by Open-Meteo.
    private static readonly Dictionary<int, string> Conditions = new()
    {
        [0] = "clear sky", [1] = "mostly clear", [2] = "partly cloudy", [3] = "overcast",
        [45] = "fog", [48] = "freezing fog",
        [51] = "light drizzle", [53] = "drizzle", [55] = "heavy drizzle",
        [56] = "light freezing drizzle", [57] = "freezing drizzle",
        [61] = "light rain", [63] = "rain", [65] = "heavy rain",
        [66] = "light freezing rain", [67] = "freezing rain",
        [71] = "light snow", [73] = "snow", [75] = "heavy snow", [77] = "snow grains",
        [80] = "light rain showers", [81] = "rain showers", [82] = "heavy rain showers",
        [85] = "light snow showers", [86] = "heavy snow showers",
        [95] = "a thunderstorm", [96] = "a thunderstorm with hail", [99] = "a severe thunderstorm with hail",
    };

    private static string Describe(int code) => Conditions.TryGetValue(code, out var text) ? text : "unclear conditions";

    /// <param name="when">"today" or "tomorrow" (anything else is treated as today).</param>
    public async Task<string> DescribeAsync(string? when)
    {
        try
        {
            var loc = await ResolveLocationAsync();
            if (loc == null)
                return "Could not work out your location for the weather. Set RAPHAEL_WEATHER_LOCATION to your city and restart.";

            var url = "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={loc.Value.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + $"&longitude={loc.Value.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + "&current=temperature_2m,weather_code"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                + "&timezone=auto&forecast_days=2";

            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return $"Could not fetch the weather ({(int)response.StatusCode}).";
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            var tomorrow = string.Equals((when ?? "today").Trim(), "tomorrow", StringComparison.OrdinalIgnoreCase);
            var daily = json["daily"]!;
            var i = tomorrow ? 1 : 0;

            var hi = Math.Round(daily["temperature_2m_max"]![i]!.GetValue<double>());
            var lo = Math.Round(daily["temperature_2m_min"]![i]!.GetValue<double>());
            var rain = daily["precipitation_probability_max"]?[i]?.GetValue<int?>();
            var condition = Describe(daily["weather_code"]![i]!.GetValue<int>());
            var rainText = rain is > 20 ? $", {rain}% chance of rain" : "";

            if (!tomorrow && json["current"] != null)
            {
                var now = Math.Round(json["current"]!["temperature_2m"]!.GetValue<double>());
                var nowCondition = Describe(json["current"]!["weather_code"]!.GetValue<int>());
                return $"In {loc.Value.City}: {now}°C and {nowCondition} right now. Today's high {hi}°C, low {lo}°C{rainText}.";
            }
            return $"In {loc.Value.City} tomorrow: {condition}, high {hi}°C, low {lo}°C{rainText}.";
        }
        catch (Exception ex)
        {
            return $"Could not fetch the weather: {ex.Message}";
        }
    }

    private async Task<(double Lat, double Lon, string City)?> ResolveLocationAsync()
    {
        var wanted = Environment.GetEnvironmentVariable("RAPHAEL_WEATHER_LOCATION")?.Trim();

        try
        {
            if (File.Exists(CachePath))
            {
                var cached = JsonNode.Parse(await File.ReadAllTextAsync(CachePath));
                var sameRequest = string.IsNullOrEmpty(wanted)
                    ? cached?["source"]?.GetValue<string>() == "ip"
                    : string.Equals(cached?["requested"]?.GetValue<string>(), wanted, StringComparison.OrdinalIgnoreCase);
                if (sameRequest && cached?["lat"] != null)
                    return (cached["lat"]!.GetValue<double>(), cached["lon"]!.GetValue<double>(), cached["city"]!.GetValue<string>());
            }
        }
        catch { /* fall through and look it up again */ }

        var found = string.IsNullOrEmpty(wanted) ? await LocateByIpAsync() : await GeocodeAsync(wanted);
        if (found == null) return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, new JsonObject
            {
                ["source"] = string.IsNullOrEmpty(wanted) ? "ip" : "name",
                ["requested"] = wanted,
                ["lat"] = found.Value.Lat, ["lon"] = found.Value.Lon, ["city"] = found.Value.City
            }.ToJsonString());
        }
        catch { /* worst case it looks itself up again next time */ }

        return found;
    }

    /// <summary>Turns a city name into coordinates (Open-Meteo's free geocoder).</summary>
    private async Task<(double Lat, double Lon, string City)?> GeocodeAsync(string place)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(place)}";
        using var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var result = json?["results"]?[0];
        if (result == null) return null;

        var city = result["name"]!.GetValue<string>();
        var country = result["country"]?.GetValue<string>();
        return (result["latitude"]!.GetValue<double>(), result["longitude"]!.GetValue<double>(),
            country != null ? $"{city}, {country}" : city);
    }

    /// <summary>Roughly where you are, from your public IP address. No sign-in, key or personal data involved.</summary>
    private async Task<(double Lat, double Lon, string City)?> LocateByIpAsync()
    {
        using var response = await _http.GetAsync("https://ipwho.is/");
        if (!response.IsSuccessStatusCode) return null;
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (json?["success"]?.GetValue<bool>() != true) return null;

        var city = json["city"]?.GetValue<string>();
        var country = json["country"]?.GetValue<string>();
        var label = city != null && country != null ? $"{city}, {country}" : city ?? country ?? "your area";
        return (json["latitude"]!.GetValue<double>(), json["longitude"]!.GetValue<double>(), label);
    }
}
