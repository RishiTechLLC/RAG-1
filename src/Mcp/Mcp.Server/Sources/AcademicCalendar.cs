using System.Text.Json;

namespace Mcp.Server.Sources;

public sealed record CalendarEvent(DateOnly Date, string Term, string Title, string Category);

/// <summary>Data source #4: a static JSON file shipped with the server (academic calendar).</summary>
public sealed class AcademicCalendar
{
    private readonly IReadOnlyList<CalendarEvent> _events;

    public AcademicCalendar(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "data", "academic-calendar.json");
        _events = File.Exists(path)
            ? JsonSerializer.Deserialize<List<CalendarEvent>>(File.ReadAllText(path), JsonSerializerOptions.Web) ?? []
            : [];
    }

    public IEnumerable<CalendarEvent> Query(string? term, string? category, DateOnly? from, DateOnly? to) =>
        _events.Where(e =>
                (term is null || e.Term.Equals(term, StringComparison.OrdinalIgnoreCase)) &&
                (category is null || e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) &&
                (from is null || e.Date >= from) &&
                (to is null || e.Date <= to))
            .OrderBy(e => e.Date);
}
