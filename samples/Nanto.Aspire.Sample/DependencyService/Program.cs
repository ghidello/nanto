using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddOtlpExporter());

var app = builder.Build();
app.MapGet("/ping", () => "pong");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.Run();
