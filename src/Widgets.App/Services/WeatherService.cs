using System.Globalization;
using System.Text.Json;

namespace Widgets.App.Services;

public sealed record WeatherSnapshot(
    double TemperatureC,
    double FeelsLikeC,
    int WeatherCode,
    double WindKph,
    int Humidity,
    double PrecipitationMm,
    DateTimeOffset Sunrise,
    DateTimeOffset Sunset,
    IReadOnlyList<DailyForecast> Daily,
    DateTimeOffset FetchedAt);

public sealed record DailyForecast(DateOnly Date, double HighC, double LowC, int WeatherCode, int PrecipProbability);

public sealed record GeoResult(string Name, string Admin, string Country, double Latitude, double Longitude);

/// <summary>
/// Talks to Open-Meteo (no API key required). Every network call is best-effort: failures are
/// logged and turned into <c>null</c> so a flaky connection never crashes a widget.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<(double Lat, double Lon), WeatherSnapshot> _cache = new();

    public async Task<WeatherSnapshot?> GetAsync(double lat, double lon, CancellationToken ct = default)
    {
        var key = (Math.Round(lat, 2), Math.Round(lon, 2));

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.Now - cached.FetchedAt < CacheDuration)
            {
                return cached;
            }
        }

        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max&timezone=auto&forecast_days=7");

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var offset = TimeSpan.FromSeconds(root.GetProperty("utc_offset_seconds").GetInt32());
            DateTimeOffset ParseLocal(string s)
                => new(DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None), offset);

            var current = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var highs = daily.GetProperty("temperature_2m_max");
            var lows = daily.GetProperty("temperature_2m_min");
            var sunrises = daily.GetProperty("sunrise");
            var sunsets = daily.GetProperty("sunset");
            var precipProb = daily.GetProperty("precipitation_probability_max");

            var forecasts = new List<DailyForecast>();
            for (var i = 0; i < dates.GetArrayLength(); i++)
            {
                forecasts.Add(new DailyForecast(
                    DateOnly.ParseExact(dates[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    highs[i].GetDouble(),
                    lows[i].GetDouble(),
                    codes[i].GetInt32(),
                    precipProb[i].ValueKind == JsonValueKind.Number ? precipProb[i].GetInt32() : 0));
            }

            var snapshot = new WeatherSnapshot(
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("apparent_temperature").GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.GetProperty("wind_speed_10m").GetDouble(),
                current.GetProperty("relative_humidity_2m").GetInt32(),
                current.GetProperty("precipitation").GetDouble(),
                ParseLocal(sunrises[0].GetString()!),
                ParseLocal(sunsets[0].GetString()!),
                forecasts,
                DateTimeOffset.Now);

            lock (_cacheGate)
            {
                _cache[key] = snapshot;
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WeatherService.GetAsync");
            return null;
        }
    }

    public async Task<IReadOnlyList<GeoResult>> SearchLocationAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=8&language=ja&format=json";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var results))
            {
                return [];
            }

            var list = new List<GeoResult>();
            foreach (var item in results.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var admin = item.TryGetProperty("admin1", out var a) ? a.GetString() ?? string.Empty : string.Empty;
                var country = item.TryGetProperty("country", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var lat = item.GetProperty("latitude").GetDouble();
                var lon = item.GetProperty("longitude").GetDouble();
                list.Add(new GeoResult(name, admin, country, lat, lon));
            }

            return list;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WeatherService.SearchLocationAsync");
            return [];
        }
    }

    public async Task<GeoResult?> DetectLocationAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("http://ip-api.com/json/", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() != "success")
            {
                return null;
            }

            var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() ?? string.Empty : string.Empty;
            var region = root.TryGetProperty("regionName", out var regionEl) ? regionEl.GetString() ?? string.Empty : string.Empty;
            var country = root.TryGetProperty("country", out var countryEl) ? countryEl.GetString() ?? string.Empty : string.Empty;
            var lat = root.GetProperty("lat").GetDouble();
            var lon = root.GetProperty("lon").GetDouble();

            return new GeoResult(city, region, country, lat, lon);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WeatherService.DetectLocationAsync");
            return null;
        }
    }

    /// <summary>Japanese description for a WMO weather code (as used by Open-Meteo).</summary>
    public static string DescribeWeatherCode(int code) => code switch
    {
        0 => "快晴",
        1 => "ほぼ晴れ",
        2 => "晴れ時々曇り",
        3 => "曇り",
        45 => "霧",
        48 => "霧氷",
        51 => "小雨",
        53 => "霧雨",
        55 => "強い霧雨",
        56 => "着氷性の弱い霧雨",
        57 => "着氷性の霧雨",
        61 => "小雨",
        63 => "雨",
        65 => "強い雨",
        66 => "着氷性の弱い雨",
        67 => "着氷性の雨",
        71 => "小雪",
        73 => "雪",
        75 => "大雪",
        77 => "霧雪",
        80 => "にわか雨",
        81 => "にわか雨",
        82 => "激しいにわか雨",
        85 => "にわか雪",
        86 => "激しいにわか雪",
        95 => "雷雨",
        96 => "雷雨（ひょうを伴う）",
        99 => "雷雨（激しいひょうを伴う）",
        _ => "不明",
    };

    /// <summary>Segoe Fluent Icons glyph for a WMO weather code.</summary>
    /// <summary>
    /// Weather icons come from the emoji font rather than Segoe Fluent Icons. The icon font ships
    /// no weather-condition set — E9C0-E9CF is unassigned — so those codepoints render as tofu.
    /// </summary>
    public static string GlyphForWeatherCode(int code, bool isNight) => code switch
    {
        0 => isNight ? "\uD83C\uDF19" : "\u2600\uFE0F",
        1 or 2 => isNight ? "\u2601\uFE0F" : "\u26C5",
        3 => "\u2601\uFE0F",
        45 or 48 => "\uD83C\uDF2B\uFE0F",
        51 or 53 or 55 or 56 or 57 => "\uD83C\uDF26\uFE0F",
        61 or 63 or 65 or 66 or 67
            or 80 or 81 or 82 => "\uD83C\uDF27\uFE0F",
        71 or 73 or 75 or 77 or 85 or 86 => "\uD83C\uDF28\uFE0F",
        95 or 96 or 99 => "\u26C8\uFE0F",
        _ => "\u2601\uFE0F",
    };

    public void Dispose() => _http.Dispose();
}
