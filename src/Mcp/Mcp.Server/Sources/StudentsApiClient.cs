using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcp.Server.Sources;

/// <summary>Data source #1: the Students Web API (SQL Server behind it).</summary>
public sealed class StudentsApiClient(HttpClient http)
{
    public Task<JsonNode?> SearchStudentsAsync(string? search, string? major, string? status, int page, int pageSize, CancellationToken ct) =>
        GetAsync($"api/students?search={Uri.EscapeDataString(search ?? string.Empty)}&major={Uri.EscapeDataString(major ?? string.Empty)}&status={Uri.EscapeDataString(status ?? string.Empty)}&page={page}&pageSize={pageSize}", ct);

    public Task<JsonNode?> GetStudentAsync(int id, CancellationToken ct) => GetAsync($"api/students/{id}", ct);

    public Task<JsonNode?> GetStudentByNumberAsync(string studentNumber, CancellationToken ct) =>
        GetAsync($"api/students/by-number/{Uri.EscapeDataString(studentNumber)}", ct);

    public Task<JsonNode?> GetEnrollmentsAsync(int id, CancellationToken ct) => GetAsync($"api/students/{id}/enrollments", ct);

    public Task<JsonNode?> GetCoursesAsync(string? department, CancellationToken ct) =>
        GetAsync($"api/courses?department={Uri.EscapeDataString(department ?? string.Empty)}", ct);

    public Task<JsonNode?> GetCourseRosterAsync(string code, string? term, CancellationToken ct) =>
        GetAsync($"api/courses/{Uri.EscapeDataString(code)}/students?term={Uri.EscapeDataString(term ?? string.Empty)}", ct);

    public Task<JsonNode?> GetMajorStatsAsync(CancellationToken ct) => GetAsync("api/stats/majors", ct);

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        // Strip empty query parameters so optional filters are really optional.
        var cleaned = string.Join('&', path.Split('&').Where(p => !p.EndsWith('=')));
        cleaned = cleaned.EndsWith('?') ? cleaned[..^1] : cleaned;

        using var response = await http.GetAsync(cleaned, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }
}
