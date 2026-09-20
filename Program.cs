using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Common.Diagnostics;
using Common.Registration;
using Common.Secrets;

var builder = WebApplication.CreateBuilder(args);

string configurationUrl = builder.Configuration["Aegis:Configuration:Url"]
    ?? "https://config.ffpja.org/";
string diagnosticsUrl = builder.Configuration["Aegis:Diagnostics:Url"]
    ?? string.Empty;

builder.Services.AddHttpClient("configuration-registration");
builder.Services.AddHttpClient("diagnostics");
builder.Services.AddCommonSecrets(builder.Configuration);
string operationsLogInstanceId = builder.Configuration["Aegis:Registration:InstanceId"]?.Trim()
    ?? builder.Environment.EnvironmentName;
string operationsLogIdentityFile = builder.Configuration["Aegis:Registration:IdentityFile"]?.Trim()
    ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Hello", "registration-identity.json")
        : "/var/lib/aegis/hello/registration-identity.json");
builder.Services.AddAegisOperationsLogging(builder.Configuration, "Aegis.Hello", operationsLogInstanceId, operationsLogIdentityFile, builder.Environment.EnvironmentName);
builder.Services.AddHostedService<HelloOperationsTelemetryPublisher>();

var app = builder.Build();

const string applicationId = "Aegis.Hello";
const string component = "Web";
string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
string environmentName = app.Environment.EnvironmentName;
string hostName = Dns.GetHostName();
string instanceId = builder.Configuration["Aegis:Registration:InstanceId"]?.Trim()
    ?? environmentName;
string identityFile = builder.Configuration["Aegis:Registration:IdentityFile"]?.Trim()
    ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Hello", "registration-identity.json")
        : "/var/lib/aegis/hello/registration-identity.json");

var stateGate = new object();
RegistrationLifecycleStatus? lastRegistration = null;
HttpStatusCode? lastContractStatus = null;
DateTimeOffset? lastContractAttemptAtUtc = null;
var timeline = new Queue<TimelineEvent>();

void Record(string message, string? correlationId = null)
{
    lock (stateGate)
    {
        timeline.Enqueue(new(DateTimeOffset.UtcNow, message, correlationId));
        while (timeline.Count > 50)
            timeline.Dequeue();
    }
}


async Task<JsonObject> BuildContractAsync(CancellationToken ct)
{
    bool requiredConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]);
    bool optionalConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:OptionalLabel"]);
    string? publicUrl = builder.Configuration["Aegis:PublicUrl"];
    bool publicUrlConfigured = !string.IsNullOrWhiteSpace(publicUrl);
    CommonSecretsHealth secretsHealth = await app.Services
        .GetRequiredService<ICommonSecretsHealthCheck>()
        .CheckAsync(ct);

    return new JsonObject
    {
        ["applicationId"] = applicationId,
        ["displayName"] = "Aegis Hello",
        ["version"] = version,
        ["description"] = "Permanent registration and diagnostics contract test application.",
        ["presentation"] = new JsonObject
        {
            ["iconUrl"] = builder.Configuration["Aegis:Presentation:IconUrl"],
            ["shortName"] = "Hello",
            ["accent"] = builder.Configuration["Aegis:Presentation:Accent"] ?? "gold"
        },
        ["runbookReference"] = "docs/RUNBOOK.md",
        ["siteId"] = builder.Configuration["Site:Id"],
        ["instanceId"] = instanceId,
        ["runtimeEnvironment"] = new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["hostName"] = hostName,
            ["hostingEnvironment"] = environmentName,
            ["operatingSystem"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["osArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount
        },
        ["diagnostics"] = new JsonObject
        {
            ["baseUrlConfigurationKey"] = "Aegis:PublicUrl",
            ["healthPath"] = "/health",
            ["supportsRemoteDiagnostics"] = false,
            ["supportsOperationalTelemetry"] = true,
            ["authenticationScheme"] = "AegisRegistration",
            ["supportedLevels"] = new JsonArray(1, 2, 3, 4, 5)
        },
        ["secrets"] = new JsonObject
        {
            ["provider"] = secretsHealth.ProviderName,
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
                ["configurationState"] = publicUrlConfigured ? "Configured" : "Missing"
            },
            new JsonObject
            {
                ["id"] = "required-message",
                ["displayName"] = "Required Message",
                ["kind"] = "Configuration",
                ["required"] = true,
                ["purpose"] = "Proves required configuration state reporting without exposing its value.",
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

async Task<JsonObject> BuildMetadataAsync(CancellationToken ct)
{
    CommonSecretsHealth secretsHealth = await app.Services
        .GetRequiredService<ICommonSecretsHealthCheck>()
        .CheckAsync(ct);

    return new JsonObject
    {
        ["displayName"] = "Aegis Hello",
        ["presentation"] = new JsonObject
        {
            ["iconUrl"] = builder.Configuration["Aegis:Presentation:IconUrl"],
            ["shortName"] = "Hello",
            ["accent"] = builder.Configuration["Aegis:Presentation:Accent"] ?? "gold"
        },
        ["applicationVersion"] = version,
        ["environment"] = environmentName,
        ["hostname"] = hostName,
        ["operatingSystem"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        ["framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["publicUrl"] = builder.Configuration["Aegis:PublicUrl"],
        ["secretsProvider"] = secretsHealth.ProviderName,
        ["capabilities"] = new JsonArray("health", "registration-reference", "diagnostics-probe")
    };
}

RegistrationLifecycleClient CreateRegistrationClient()
{
    if (!Uri.TryCreate(configurationUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri))
        throw new InvalidOperationException("Aegis Configuration URL is invalid.");

    return new RegistrationLifecycleClient(
        app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("configuration-registration"),
        new RegistrationLifecycleOptions(
            baseUri,
            applicationId,
            instanceId,
            identityFile,
            TimeSpan.FromSeconds(10),
            RunbookReference: "docs/RUNBOOK.md"),
        logger: app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Aegis.Hello.Registration"));
}

async Task<RegistrationLifecycleStatus> RegistrationStepAsync(CancellationToken ct)
{
    JsonObject metadata = await BuildMetadataAsync(ct);
    RegistrationLifecycleClient registration = CreateRegistrationClient();
    RegistrationLifecycleStatus result = await registration.StepAsync(metadata, ct);

    if (result.IsRegistered)
    {
        HttpStatusCode contractStatus = await registration.PublishConfigurationContractAsync(
            await BuildContractAsync(ct),
            ct);

        lock (stateGate)
        {
            lastContractStatus = contractStatus;
            lastContractAttemptAtUtc = DateTimeOffset.UtcNow;
        }

        if ((int)contractStatus is >= 200 and < 300)
            Record("Configuration contract published", result.CorrelationId);
        else
            Record($"Configuration contract publication failed: HTTP {(int)contractStatus}", result.CorrelationId);
    }

    lock (stateGate)
        lastRegistration = result;

    Record($"Registration state: {result.State}", result.CorrelationId);
    return result;
}

app.MapAegisHealth(applicationId);

app.MapGet("/", () =>
{
    RegistrationLifecycleStatus? registration;
    TimelineEvent[] events;
    lock (stateGate)
    {
        registration = lastRegistration;
        events = timeline.Reverse().Take(12).Reverse().ToArray();
    }

    string state = registration?.State.ToString() ?? "Starting";
    string stateClass = registration?.State switch
    {
        RegistrationLifecycleState.Registered => "good",
        RegistrationLifecycleState.Pending or RegistrationLifecycleState.RecoveryPending => "warn",
        RegistrationLifecycleState.Unregistered => "neutral",
        _ => "bad"
    };

    string eventRows = events.Length == 0
        ? "<div class='empty'>No lifecycle events recorded yet.</div>"
        : string.Join("", events.Select(x =>
            $"<div class='event'><time class='local-datetime' datetime='{E(x.AtUtc.ToString("O"))}'>{E(x.AtUtc.ToString("u"))}</time><span>{E(x.Message)}</span><code>{E(x.CorrelationId)}</code></div>"));

    string error = string.IsNullOrWhiteSpace(registration?.Error)
        ? string.Empty
        : $"<div class='error'><b>Registration detail</b><span>{E(registration.Error)}</span></div>";

    string html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Aegis Hello</title>
<style>
body{margin:0;background:#071522;color:#edf7ff;font:14px Segoe UI,Arial,sans-serif}main{max-width:1050px;margin:35px auto;padding:0 22px}
h1{font-size:30px;margin-bottom:4px}.muted{color:#9fb7c8}.grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}.panel{background:#0d2030;border:1px solid #28597b;border-radius:10px;padding:18px;margin:16px 0}
.facts{display:grid;grid-template-columns:1fr 1fr;gap:12px}.fact{background:#081824;border-radius:7px;padding:12px}.fact small{display:block;color:#91aabd;margin-bottom:4px}.fact b{word-break:break-word}
.badge{display:inline-block;padding:7px 11px;border-radius:999px;font-weight:800}.good{background:#145b40}.warn{background:#72531b}.bad{background:#7d2f2f}.neutral{background:#34495a}
.event{display:grid;grid-template-columns:80px 1fr 220px;gap:12px;padding:9px 0;border-bottom:1px solid #29475b}.event code{font-size:11px;color:#9fc9e8;overflow:hidden;text-overflow:ellipsis}.error{display:flex;gap:10px;background:#4a2020;border:1px solid #8d3d3d;padding:12px;border-radius:7px}.empty{color:#91aabd}
a{color:#72c4ff}@media(max-width:720px){.grid,.facts{grid-template-columns:1fr}.event{grid-template-columns:70px 1fr}.event code{display:none}}
</style></head>
<body><main>
<h1>Aegis Hello</h1><p class="muted">Registration reference implementation and service status</p>
<div class="grid">
<section class="panel"><h2>Identity</h2><div class="facts">
<div class="fact"><small>APPLICATION</small><b>__APPLICATION__</b></div>
<div class="fact"><small>INSTANCE</small><b>__INSTANCE__</b></div>
<div class="fact"><small>INSTALLATION ID</small><b>__INSTALLATION__</b></div>
<div class="fact"><small>VERSION</small><b>__VERSION__</b></div>
<div class="fact"><small>HOST</small><b>__HOST__</b></div>
<div class="fact"><small>ENVIRONMENT</small><b>__ENVIRONMENT__</b></div>
</div></section>
<section class="panel"><h2>Registration</h2><p><span class="badge __STATE_CLASS__">__STATE__</span></p>
<div class="facts"><div class="fact"><small>REGISTRATION ID</small><b>__REGISTRATION_ID__</b></div>
<div class="fact"><small>LAST OBSERVED</small><b id="lastObserved" data-utc="__LAST_OBSERVED_ISO__">__LAST_OBSERVED__</b></div></div>__ERROR__</section>
</div>
<section class="panel"><h2>Connectivity</h2><div class="facts">
<div class="fact"><small>CONFIGURATION</small><b>__CONFIGURATION_URL__</b></div>
<div class="fact"><small>DIAGNOSTICS</small><b>__DIAGNOSTICS_URL__</b></div>
</div></section>
<section class="panel"><h2>Lifecycle timeline</h2>__EVENT_ROWS__</section>
<p class="muted">Machine-readable status: <a href="/status.json">/status.json</a>. No credentials or claim tokens are exposed.</p>
<script>
const localDateTime=v=>{const d=new Date(v);return Number.isNaN(d.getTime())?String(v??'—'):d.toLocaleString(undefined,{dateStyle:'medium',timeStyle:'medium'});};
const localZone=()=>Intl.DateTimeFormat().resolvedOptions().timeZone;
document.querySelectorAll('time.local-datetime[datetime]').forEach(x=>{x.textContent=localDateTime(x.getAttribute('datetime'));x.title=localZone();});
const observed=document.getElementById('lastObserved');if(observed?.dataset.utc){observed.textContent=localDateTime(observed.dataset.utc)+' · '+localZone();}
</script>
</main></body></html>
"""
        .Replace("__APPLICATION__", E(applicationId), StringComparison.Ordinal)
        .Replace("__INSTANCE__", E(instanceId), StringComparison.Ordinal)
        .Replace("__INSTALLATION__", E(registration?.Identity.InstallationId), StringComparison.Ordinal)
        .Replace("__VERSION__", E(version), StringComparison.Ordinal)
        .Replace("__HOST__", E(hostName), StringComparison.Ordinal)
        .Replace("__ENVIRONMENT__", E(environmentName), StringComparison.Ordinal)
        .Replace("__STATE_CLASS__", stateClass, StringComparison.Ordinal)
        .Replace("__STATE__", E(state), StringComparison.Ordinal)
        .Replace("__REGISTRATION_ID__", E(registration?.RegistrationId), StringComparison.Ordinal)
        .Replace("__LAST_OBSERVED__", E(registration?.ObservedAtUtc.ToString("u")), StringComparison.Ordinal)
        .Replace("__LAST_OBSERVED_ISO__", E(registration?.ObservedAtUtc.ToString("O")), StringComparison.Ordinal)
        .Replace("__ERROR__", error, StringComparison.Ordinal)
        .Replace("__CONFIGURATION_URL__", E(configurationUrl), StringComparison.Ordinal)
        .Replace("__DIAGNOSTICS_URL__", E(diagnosticsUrl), StringComparison.Ordinal)
        .Replace("__EVENT_ROWS__", eventRows, StringComparison.Ordinal);

    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/status.json", () =>
{
    RegistrationLifecycleStatus? registration;
    HttpStatusCode? contractStatus;
    DateTimeOffset? contractAttemptAt;
    TimelineEvent[] events;
    lock (stateGate)
    {
        registration = lastRegistration;
        contractStatus = lastContractStatus;
        contractAttemptAt = lastContractAttemptAtUtc;
        events = timeline.ToArray();
    }

    return Results.Ok(new
    {
        application = applicationId,
        component,
        version,
        environment = environmentName,
        host = hostName,
        instanceId,
        installationId = registration?.Identity.InstallationId,
        registration = registration is null ? null : new
        {
            state = registration.State.ToString(),
            registration.RegistrationId,
            registration.ObservedAtUtc,
            registration.Error,
            registration.StatusCode,
            registration.CorrelationId
        },
        configurationContract = new
        {
            lastStatusCode = contractStatus is null ? null : (int?)contractStatus,
            healthy = contractStatus is not null && (int)contractStatus is >= 200 and < 300,
            lastAttemptAtUtc = contractAttemptAt
        },
        timeline = events
    });
});

app.MapPost("/registration/step", async (CancellationToken ct) =>
{
    RegistrationLifecycleStatus result = await RegistrationStepAsync(ct);
    return result.State is RegistrationLifecycleState.Registered or
        RegistrationLifecycleState.Pending or
        RegistrationLifecycleState.RecoveryPending
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/config-state", () =>
{
    bool requiredConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:RequiredMessage"]);
    bool optionalConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Hello:OptionalLabel"]);
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
        return Results.Problem("Aegis Diagnostics URL is not configured.", statusCode: 503);

    string normalized = level.ToLowerInvariant();
    if (normalized is not ("info" or "warning" or "error"))
        return Results.BadRequest(new { error = "Level must be info, warning, or error." });

    if (!Uri.TryCreate(diagnosticsUrl.TrimEnd('/') + "/health", UriKind.Absolute, out Uri? uri))
        return Results.Problem("Aegis Diagnostics URL is invalid.", statusCode: 503);

    try
    {
        using HttpResponseMessage response = await clients.CreateClient("diagnostics").GetAsync(uri, ct);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { reachable = true, endpoint = uri.ToString(), requestedLevel = normalized })
            : Results.Problem($"Diagnostics returned HTTP {(int)response.StatusCode}.", statusCode: 503);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Problem($"Diagnostics health check failed: {ex.Message}", statusCode: 503);
    }
});

Record("Application started");

app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        CancellationToken stopping = app.Lifetime.ApplicationStopping;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                RegistrationLifecycleStatus result = await RegistrationStepAsync(stopping);
                TimeSpan delay = result.State switch
                {
                    RegistrationLifecycleState.Registered => TimeSpan.FromMinutes(5),
                    RegistrationLifecycleState.Pending or RegistrationLifecycleState.RecoveryPending => TimeSpan.FromSeconds(10),
                    RegistrationLifecycleState.IdentityConflict or RegistrationLifecycleState.Rejected => TimeSpan.FromMinutes(1),
                    _ => TimeSpan.FromSeconds(30)
                };
                await Task.Delay(delay, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Record("Registration error: " + ex.GetType().Name);
                app.Logger.LogWarning(ex, "Hello registration lifecycle step failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(30), stopping); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            }
        }
    });
});

app.Run();

static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "—");
internal sealed record TimelineEvent(DateTimeOffset AtUtc, string Message, string? CorrelationId);

internal sealed class HelloOperationsTelemetryPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<HelloOperationsTelemetryPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "Aegis.Hello";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string operationsUrl = configuration["Aegis:Operations:Url"]?.Trim()
            ?? "https://operations.ffpja.org";
        string instanceId = configuration["Aegis:Registration:InstanceId"]?.Trim()
            ?? environment.EnvironmentName;
        string identityFile = configuration["Aegis:Registration:IdentityFile"]?.Trim()
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Hello", "registration-identity.json")
                : "/var/lib/aegis/hello/registration-identity.json");

        var identityStore = new FileRegistrationIdentityStore(identityFile);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RegistrationIdentityDocument identity =
                    await identityStore.LoadOrCreateAsync(ApplicationId, instanceId, stoppingToken);

                if (string.IsNullOrWhiteSpace(identity.Credential))
                {
                    logger.LogDebug("Hello operational telemetry is waiting for a durable registration credential.");
                }
                else
                {
                    using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        operationsUrl.TrimEnd('/') + "/api/operations/applications/observe")
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(new
                        {
                            applicationId = ApplicationId,
                            displayName = "Aegis Hello",
                            instanceId,
                            environment = environment.EnvironmentName,
                            version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                            registrationStatus = "Registered",
                            state = "Healthy",
                            observedAtUtc = DateTimeOffset.UtcNow,
                            configurationStatus = "Published",
                            dependencies = new[]
                            {
                                configuration["Aegis:Configuration:Url"] ?? "https://config.ffpja.org/"
                            },
                            reason = "Hello process heartbeat is current and its durable registration credential is valid."
                        })
                    };

                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", identity.Credential);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

                    using HttpResponseMessage response = await client.SendAsync(request, stoppingToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogWarning(
                            "Hello operational heartbeat returned HTTP {StatusCode}.",
                            (int)response.StatusCode);
                    }
                    else
                    {
                        logger.LogDebug(
                            "Hello operational heartbeat published as Healthy for instance {InstanceId}.",
                            instanceId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hello operational heartbeat publication failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
