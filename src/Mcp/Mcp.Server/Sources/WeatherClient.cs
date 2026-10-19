using System.Text.Json.Nodes;

namespace Mcp.Server.Sources;

/// <summary>Data source #3: Open-Meteo public weather API (no API key required).</summary>
public sealed class WeatherClient(HttpClient http)
{
    public async Task<object?> GetCurrentWeatherAsync(string city, CancellationToken ct)
    {
        var geo = await http.GetFromJsonAsync<JsonNode>(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1", ct);
        var place = geo?["results"]?[0];
        if (place is null)
        {
            return null;
        }

        var lat = place["latitude"]!.GetValue<double>();
        var lon = place["longitude"]!.GetValue<double>();
        var forecast = await http.GetFromJsonAsync<JsonNode>(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,relative_humidity_2m,wind_speed_10m,weather_code" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=3&timezone=auto", ct);

        return new
        {
            location = $"{place["name"]}, {place["country"]}",
            current = forecast?["current"],
            daily = forecast?["daily"],
            units = forecast?["current_units"],
        };
    }
}
