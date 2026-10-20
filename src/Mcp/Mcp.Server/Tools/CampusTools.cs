using System.ComponentModel;
using Mcp.Server.Sources;
using ModelContextProtocol.Server;

namespace Mcp.Server.Tools;

[McpServerToolType]
public sealed class CampusTools(AcademicCalendar calendar, WeatherClient weather, TimeProvider time)
{
    [McpServerTool(Name = "get_academic_calendar", ReadOnly = true), Description("Academic calendar events (term start/end, add/drop deadlines, holidays, exams). Filter by term (2025-FA, 2026-SP, 2026-FA), category (Classes, Registration, Holiday, Exams, Ceremony) or date range (yyyy-MM-dd).")]
    public IEnumerable<CalendarEvent> GetAcademicCalendar(
        string? term = null,
        string? category = null,
        [Description("Inclusive start date yyyy-MM-dd.")] string? from = null,
        [Description("Inclusive end date yyyy-MM-dd.")] string? to = null) =>
        calendar.Query(
            term,
            category,
            DateOnly.TryParse(from, out var f) ? f : null,
            DateOnly.TryParse(to, out var t) ? t : null);

    [McpServerTool(Name = "get_upcoming_events", ReadOnly = true), Description("The next academic calendar events from today.")]
    public IEnumerable<CalendarEvent> GetUpcomingEvents([Description("How many events to return.")] int count = 5) =>
        calendar.Query(null, null, DateOnly.FromDateTime(time.GetLocalNow().Date), null).Take(Math.Clamp(count, 1, 50));

    [McpServerTool(Name = "get_weather", ReadOnly = true, OpenWorld = true), Description("Current weather and a 3-day forecast for a city (live data from Open-Meteo).")]
    public async Task<object> GetWeather(
        [Description("City name, e.g. 'Seattle'.")] string city,
        CancellationToken cancellationToken = default) =>
        await weather.GetCurrentWeatherAsync(city, cancellationToken) ?? $"Could not find a city named '{city}'.";
}
