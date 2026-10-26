using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Cross-cutting concerns shared by every service: OpenTelemetry (OTLP locally via the Aspire
/// dashboard, Application Insights in Azure), health checks, resilient HttpClients and service discovery.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        // UpstreamExceptionHandler writes through IProblemDetailsService, so register it here for every app
        // (AddProblemDetails is idempotent; apps that also call it are unaffected).
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<UpstreamExceptionHandler>();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler(options =>
            {
                // LLM-backed calls (agents, MCP tools) can legitimately take a while.
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(3);
            });
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource("Microsoft.Agents.AI*", "Microsoft.Extensions.AI*", "ModelContextProtocol*")
                .AddAspNetCoreInstrumentation(t =>
                    t.Filter = ctx => !ctx.Request.Path.StartsWithSegments(HealthEndpointPath)
                                   && !ctx.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation());

        // Local: the Aspire dashboard injects OTEL_EXPORTER_OTLP_ENDPOINT.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Azure: App Service sets APPLICATIONINSIGHTS_CONNECTION_STRING (see infra/main.bicep).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });
        return app;
    }
}
