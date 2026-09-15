using Common.Registration;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
var configurationUrl = builder.Configuration["Aegis:ConfigurationUrl"] ?? "http://localhost:5101/";
var registrationKey = builder.Configuration["Aegis:RegistrationKey"];
var diagnosticsUrl = builder.Configuration["Aegis:DiagnosticsUrl"];

builder.Services.AddHttpClient("configuration");
builder.Services.AddHttpClient("diagnostics");

var app = builder.Build();

var identity = ComponentIdentity.Detect(
    "Aegis.Hello",
    "Web",
    new[]
    {
        new ConfigurationRequirement("Hello:RequiredMessage", true, "Required value used to prove configuration state changes."),
        new ConfigurationRequirement("Hello:OptionalLabel", false, "Optional display label.")
    },
    app.Environment.EnvironmentName);

RegistrationResult? lastRegistration = null;

app.MapGet("/", () => Results.Ok(new
{
    application = identity.Application,
    component = identity.Component,
    version = identity.Version,
    environment = identity.Environment,
    host = identity.Host,
    requiredMessageConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]),
    registration = lastRegistration
}));

app.MapPost("/register", async (IHttpClientFactory clients, CancellationToken ct) =>
{
    var registrar = new ConfigurationRegistrar(
        clients.CreateClient("configuration"),
        new RegistrationOptions(new Uri(configurationUrl), registrationKey));
    lastRegistration = await registrar.RegisterAsync(identity, ct);
    return lastRegistration.Succeeded
        ? Results.Ok(lastRegistration)
        : Results.Json(lastRegistration, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/config-state", () =>
{
    var configured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]);
    return Results.Ok(new
    {
        requirement = "Hello:RequiredMessage",
        required = true,
        configured,
        state = configured ? "configured" : "missing"
    });
});

app.MapPost("/diagnostics/{level}", async (string level, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(diagnosticsUrl))
        return Results.Problem("Aegis:DiagnosticsUrl is not configured; no diagnostic event was sent.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var normalized = level.ToLowerInvariant();
    if (normalized is not ("info" or "warning" or "error"))
        return Results.BadRequest(new { error = "Level must be info, warning, or error." });

    var payload = new
    {
        application = identity.Application,
        component = identity.Component,
        level = normalized,
        message = $"Aegis.Hello real {normalized} contract-test event.",
        occurredAtUtc = DateTimeOffset.UtcNow,
        host = identity.Host,
        environment = identity.Environment,
        version = identity.Version
    };

    try
    {
        var client = clients.CreateClient("diagnostics");
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(diagnosticsUrl), "api/events"), payload, ct);
        if (!response.IsSuccessStatusCode)
            return Results.Problem($"Diagnostics rejected the event with HTTP {(int)response.StatusCode}.", statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { sent = true, level = normalized });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("Diagnostic event timed out and was not sent.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"Diagnostic event was not sent: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
