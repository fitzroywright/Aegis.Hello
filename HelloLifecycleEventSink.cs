using System.Net.Http.Headers;
using System.Net.Http.Json;
using Common.Diagnostics;
using Common.Registration;

public sealed class HelloLifecycleEventSink(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<HelloLifecycleEventSink> logger) : ILifecycleEventSink
{
    public async Task EmitAsync(LifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
    {
        string? operationsUrl = configuration["Aegis:Operations:Url"];
        if (string.IsNullOrWhiteSpace(operationsUrl)) return;

        string identityFile = configuration["Aegis:Registration:IdentityFile"]?.Trim()
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Hello", "registration-identity.json")
                : "/var/lib/aegis/hello/registration-identity.json");

        try
        {
            var store = new FileRegistrationIdentityStore(identityFile);
            RegistrationIdentityDocument identity = await store.LoadOrCreateAsync(
                lifecycleEvent.ApplicationId, lifecycleEvent.InstanceId, cancellationToken);
            if (string.IsNullOrWhiteSpace(identity.Credential)) return;

            using HttpRequestMessage request = new(
                HttpMethod.Post,
                operationsUrl.TrimEnd('/') + "/api/operations/lifecycle/events")
            { Content = JsonContent.Create(lifecycleEvent) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
            request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", lifecycleEvent.CorrelationId);
            using HttpResponseMessage response = await clients.CreateClient("operations-lifecycle").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogDebug("Lifecycle telemetry publication returned HTTP {StatusCode}.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogDebug(ex, "Lifecycle telemetry publication failed; application lifecycle continues."); }
    }
}

