using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Context;

public sealed class WeatherService(AppConfig config) : IDisposable
{
    private readonly HttpClient client = new() { Timeout=TimeSpan.FromSeconds(8) };
    private DateTimeOffset lastFetch=DateTimeOffset.MinValue;
    private string cached="Weather unavailable (not configured).";
    public async Task<string> GetAsync(CancellationToken ct)
    {
        if(!config.Permissions.WeatherNetwork || config.Weather.Latitude is null || config.Weather.Longitude is null) return "Weather unavailable (disabled or no coordinates).";
        if(DateTimeOffset.UtcNow-lastFetch<TimeSpan.FromMinutes(config.Weather.RefreshMinutes)) return cached;
        lastFetch=DateTimeOffset.UtcNow;
        try
        {
            var lat=config.Weather.Latitude.Value.ToString(CultureInfo.InvariantCulture);var lon=config.Weather.Longitude.Value.ToString(CultureInfo.InvariantCulture);
            using var doc=await client.GetFromJsonAsync<JsonDocument>($"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,is_day,precipitation,weather_code,cloud_cover,wind_speed_10m&timezone=auto",ct);
            cached="Current weather (Open-Meteo): "+doc!.RootElement.GetProperty("current").GetRawText();
        }
        catch(Exception ex) when(ex is not OperationCanceledException || !ct.IsCancellationRequested) { cached="Weather temporarily unavailable; do not invent conditions."; }
        return cached;
    }
    public void Dispose()=>client.Dispose();
}
