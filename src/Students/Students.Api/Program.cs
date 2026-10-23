using Microsoft.EntityFrameworkCore;
using Students.Api.Data;
using Students.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ConnectionStrings:StudentsDb comes from (in order of precedence):
//   - Aspire: your on-prem server if the AppHost has ConnectionStrings:StudentsDb, else the SQL container
//   - user secrets / appsettings.Development.json when running this project on its own
//   - the App Service setting in Azure (managed identity to Azure SQL)
builder.Services.AddDbContext<StudentsDbContext>(options => options
    .UseSqlServer(
        builder.Configuration.GetConnectionString("StudentsDb")
            ?? throw new InvalidOperationException("Connection string 'StudentsDb' is not configured."),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddHealthChecks().AddDbContextCheck<StudentsDbContext>("database");
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    // Creates the database/tables if missing and seeds sample data into an empty Students table.
    // For managed production schemas switch to EF Core migrations (dotnet ef migrations add Initial).
    await using var scope = app.Services.CreateAsyncScope();
    await DatabaseInitializer.InitializeAsync(
        scope.ServiceProvider.GetRequiredService<StudentsDbContext>(),
        app.Configuration.GetValue("Database:SeedSampleData", true),
        app.Logger);
}

app.UseExceptionHandler();
app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapStudentEndpoints();

app.Run();
