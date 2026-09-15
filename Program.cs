using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Common.Registration;

var builder = WebApplication.CreateBuilder(args);
var configurationUrl = builder.Configuration["Aegis:Configuration:Url"]
    ?? builder.Configuration["Aegis:ConfigurationUrl"]
    ?? "http://localhost:5101/";
var registrationKey = builder.Configuration["Aegis:Configuration:RegistrationKey"]
    ?? builder.Configuration["Aegis:RegistrationKey"]
    ?? Environment.GetEnvironmentVariable("AEGIS_CONFIGURATION_REGISTRATION_KEY");
var diagnosticsUrl = builder.Configuration["Aegis:Diagnostics:Url"]
    ?? builder.Configuration["Aegis:DiagnosticsUrl"];

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

var stateGate = new object();
RegistrationResult? lastRegistration = null;

JsonObject BuildContract()
{
    bool requiredConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]);
    bool optionalConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:OptionalLabel"]);

    return new JsonObject
    {
        ["applicationId"] = identity.Application,
        ["displayName"] = "Aegis Hello",
        ["version"] = identity.Version,
        ["description"] = "Permanent registration and diagnostics contract test application.",
        ["runtimeEnvironment"] = new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["hostName"] = identity.Host,
            ["hostingEnvironment"] = identity.Environment,
            ["operatingSystem"] = Environment.OSVersion.ToString(),
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount
        },
        ["requirements"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "required-message",
                ["displayName"] = "Required Message",
                ["kind"] = "Configuration",
                ["required"] = true,
                ["purpose"] = "Proves that a required setting can move between missing and configured states without exposing its value.",
                ["configurationKey"] = "Hello:RequiredMessage",
                ["isConfigured"] = requiredConfigured,
                ["configurationState"] = requiredConfigured ? "Configured" : "Missing"
            },
            new JsonObject
            {
                ["id"] = "optional-label",
                ["displayName"] = "Optional Label",
                ["kind"] = "Configuration",
                ["required"] = false,
                ["purpose"] = "Proves optional configuration state reporting without exposing its value.",
                ["configurationKey"] = "Hello:OptionalLabel",
                ["isConfigured"] = optionalConfigured,
                ["configurationState"] = optionalConfigured ? "Configured" : "OptionalNotConfigured"
            }
        }
    };
}

async Task<RegistrationResult> TryRegisterAsync(CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(registrationKey))
        return RegistrationResult.Failure("Configuration registration key is not configured.");

    if (!Uri.TryCreate(configurationUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri))
        return RegistrationResult.Failure("Configuration URL is invalid.");

    var registrar = new ConfigurationRegistrar(
        app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("configuration"),
        new RegistrationOptions(baseUri, registrationKey, TimeSpan.FromSeconds(10)));

    RegistrationResult result = await registrar.RegisterContractAsync(BuildContract(), ct);
    lock (stateGate)
        lastRegistration = result;
    return result;
}

app.MapGet("/", () =>
{
    RegistrationResult? registration;
    lock (stateGate)
        registration = lastRegistration;

    return Results.Ok(new
    {
        application = identity.Application,
        component = identity.Component,
        version = identity.Version,
        environment = identity.Environment,
        host = identity.Host,
        requiredMessageConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]),
        registration
    });
});

app.MapPost("/register", async (CancellationToken ct) =>
{
    RegistrationResult result = await TryRegisterAsync(ct);
    return result.Succeeded
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/config-state", () =>
{
    var requiredConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]);
    var optionalConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:OptionalLabel"]);
    return Results.Ok(new
    {
        requirements = new object[]
        {
            new { key = "Hello:RequiredMessage", required = true, configured = requiredConfigured, state = requiredConfigured ? "configured" : "missing" },
            new { key = "Hello:OptionalLabel", required = false, configured = optionalConfigured, state = optionalConfigured ? "configured" : "optional-not-configured" }
        }
    });
});

app.MapPost("/diagnostics/{level}", async (string level, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(diagnosticsUrl))
        return Results.Problem("Aegis Diagnostics URL is not configured; no diagnostic event was sent.", statusCode: StatusCodes.Status503ServiceUnavailable);

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
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(diagnosticsUrl.TrimEnd('/') + "/"), "api/events"), payload, ct);
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

app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        CancellationToken stopping = app.Lifetime.ApplicationStopping;
        while (!stopping.IsCancellationRequested)
        {
            RegistrationResult result;
            try
            {
                result = await TryRegisterAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }

            if (result.Succeeded)
                return;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    });
});

app.Run();
