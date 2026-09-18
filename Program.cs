using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Common.Diagnostics;
using Common.Registration;
using Common.Secrets;

var builder = WebApplication.CreateBuilder(args);
var operationsUrl = builder.Configuration["Aegis:Operations:Url"]
    ?? builder.Configuration["AegisOperations:BaseUrl"]
    ?? "http://localhost:5100/";
var diagnosticsUrl = builder.Configuration["Aegis:Diagnostics:Url"]
    ?? builder.Configuration["Aegis:DiagnosticsUrl"];

builder.Services.AddHttpClient("operations");
builder.Services.AddHttpClient("diagnostics");
builder.Services.AddCommonSecrets(builder.Configuration);

var app = builder.Build();

const string applicationId = "Aegis.Hello";
const string component = "Web";
string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
string environmentName = app.Environment.EnvironmentName;
string hostName = Dns.GetHostName();

var stateGate = new object();
ApplicationRegistrationStatus? lastRegistration = null;

async Task<JsonObject> BuildContractAsync(CancellationToken ct)
{
    bool requiredConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]);
    bool optionalConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:OptionalLabel"]);
    string? publicUrl = builder.Configuration["Aegis:PublicUrl"];
    bool publicUrlConfigured = !string.IsNullOrWhiteSpace(publicUrl);
    CommonSecretsHealth secretsHealth = await app.Services.GetRequiredService<ICommonSecretsHealthCheck>().CheckAsync(ct);
    SecretProviderMetadata secretsMetadata = SecretProviderMetadataResolver.Resolve(builder.Configuration);

    return new JsonObject
    {
        ["applicationId"] = applicationId,
        ["displayName"] = "Aegis Hello",
        ["version"] = version,
        ["description"] = "Permanent registration and diagnostics contract test application.",
        ["runtimeEnvironment"] = new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["hostName"] = hostName,
            ["hostingEnvironment"] = environmentName,
            ["operatingSystem"] = Environment.OSVersion.ToString(),
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount
        },
        ["diagnostics"] = new JsonObject
        {
            ["baseUrlConfigurationKey"] = "Aegis:PublicUrl",
            ["healthPath"] = "/health",
            ["supportsRemoteDiagnostics"] = false,
            ["supportsOperationalTelemetry"] = false,
            ["authenticationScheme"] = "None",
            ["supportedLevels"] = new JsonArray(1, 2, 3, 4, 5)
        },
        ["secrets"] = new JsonObject
        {
            ["provider"] = secretsHealth.ProviderName,
            ["managementUrl"] = secretsMetadata.ManagementUrl,
            ["connectionStatus"] = secretsHealth.IsAvailable ? "Healthy" : secretsHealth.IsConfigured ? "Unavailable" : "NotConfigured",
            ["credentialStatus"] = secretsHealth.IsConfigured ? "Configured" : "NotConfigured"
        },
        ["requirements"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "public-url",
                ["displayName"] = "Public URL",
                ["kind"] = "Configuration",
                ["required"] = true,
                ["purpose"] = "Provides the network-reachable Hello endpoint that Diagnostics can discover and probe.",
                ["configurationKey"] = "Aegis:PublicUrl",
                ["isConfigured"] = publicUrlConfigured,
                ["effectiveValueAvailable"] = publicUrlConfigured,
                ["configurationState"] = publicUrlConfigured ? "Configured" : "Missing",
                ["safeDisplayValue"] = publicUrlConfigured ? publicUrl : null
            },
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

async Task<ApplicationRegistrationStatus> TryRegisterAsync(CancellationToken ct)
{
    if (!Uri.TryCreate(operationsUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri))
    {
        var failure = new ApplicationRegistrationStatus(
            ApplicationRegistrationState.Unavailable,
            DateTimeOffset.UtcNow,
            "Operations URL is invalid.");
        lock (stateGate)
            lastRegistration = failure;
        return failure;
    }

    JsonObject contract = await BuildContractAsync(ct);
    var registration = new ApplicationRegistrationClient(
        app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("operations"),
        new ApplicationRegistrationOptions(
            baseUri,
            applicationId,
            hostName,
            RegistrationCredentialResolver.EnvironmentVariableName,
            TimeSpan.FromSeconds(10)));

    ApplicationRegistrationStatus result = await registration.RegisterAsync(contract, ct);
    lock (stateGate)
        lastRegistration = result;
    return result;
}

app.MapAegisHealth(applicationId);

app.MapGet("/", () =>
{
    ApplicationRegistrationStatus? registration;
    lock (stateGate)
    {
        registration = lastRegistration;
    }

    return Results.Ok(new
    {
        application = applicationId,
        component,
        version,
        environment = environmentName,
        host = hostName,
        requiredMessageConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]),
        registrationEnvironmentVariable = RegistrationCredentialResolver.EnvironmentVariableName,
        registration
    });
});

app.MapPost("/register", async (CancellationToken ct) =>
{
    ApplicationRegistrationStatus result = await TryRegisterAsync(ct);
    return result.IsRegistered
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
        return Results.Problem("Aegis Diagnostics URL is not configured; Diagnostics availability was not checked.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var normalized = level.ToLowerInvariant();
    if (normalized is not ("info" or "warning" or "error"))
        return Results.BadRequest(new { error = "Level must be info, warning, or error." });

    if (!Uri.TryCreate(diagnosticsUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? diagnosticsBaseUri))
        return Results.Problem("Aegis Diagnostics URL is invalid; Diagnostics availability was not checked.", statusCode: StatusCodes.Status503ServiceUnavailable);

    try
    {
        var client = clients.CreateClient("diagnostics");
        using var response = await client.GetAsync(new Uri(diagnosticsBaseUri, "health"), ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return Results.Problem($"Diagnostics health endpoint returned HTTP {(int)response.StatusCode}; Diagnostics availability was not proven.", statusCode: StatusCodes.Status503ServiceUnavailable);

        JsonNode? health;
        try
        {
            health = JsonNode.Parse(responseBody);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Problem("Diagnostics health endpoint returned HTTP success but the response was not valid JSON; Diagnostics health was not proven.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new
        {
            reachable = true,
            endpoint = new Uri(diagnosticsBaseUri, "health").ToString(),
            requestedLevel = normalized,
            note = "Current Aegis.Diagnostics does not expose the legacy /api/events ingestion endpoint. This probe verifies the current public health contract and does not claim that a diagnostic event was sent.",
            diagnostics = health
        });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("Diagnostics health check timed out; Diagnostics availability was not proven.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"Diagnostics health check failed: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        CancellationToken stopping = app.Lifetime.ApplicationStopping;
        while (!stopping.IsCancellationRequested)
        {
            ApplicationRegistrationStatus result;
            try
            {
                result = await TryRegisterAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }

            if (result.IsRegistered)
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
