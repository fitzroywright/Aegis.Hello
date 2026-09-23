using Common.Diagnostics;

namespace Aegis.Hello;

public sealed class HelloRequiredMessageDiagnosticLevelTest(IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.HELLO.L5.CONFIG.REQUIRED_MESSAGE";
    public string Name => "Required Hello message configured";
    public string Owner => "Aegis.Hello";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool configured = !string.IsNullOrWhiteSpace(configuration["Hello:RequiredMessage"]);
        return Task.FromResult(configured
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Required Hello message is configured.")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Required Hello message is missing."));
    }
}

public sealed class HelloPublicUrlDiagnosticLevelTest(IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.HELLO.L5.CONFIG.PUBLIC_URL";
    public string Name => "Public URL contract valid";
    public string Owner => "Aegis.Hello";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? value = configuration["Aegis:PublicUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return Task.FromResult(EngineeringDiagnosticPolicy.Failed(TestId, Name, "Required public URL is not configured."));

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return Task.FromResult(EngineeringDiagnosticPolicy.Failed(TestId, Name, "Public URL is not an absolute URI."));

        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            return Task.FromResult(EngineeringDiagnosticPolicy.Failed(TestId, Name, "Public URL must use HTTPS except for loopback development."));

        return Task.FromResult(EngineeringDiagnosticPolicy.Passed(TestId, Name, "Public URL is configured and valid.", $"Scheme={uri.Scheme}; Host={uri.Host}"));
    }
}

public sealed class HelloControlPlaneEndpointsDiagnosticLevelTest(IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.HELLO.L4.CONTROL_PLANE.URLS";
    public string Name => "Control Plane endpoint URLs valid";
    public string Owner => "Aegis.Hello";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] keys = ["Aegis:Configuration:Url", "Aegis:Diagnostics:Url", "Aegis:Operations:Url"];
        List<string> invalid = [];
        foreach (string key in keys)
        {
            string? value = configuration[key]?.Trim();
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            {
                invalid.Add(key);
            }
        }

        return Task.FromResult(invalid.Count == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Control Plane endpoint URLs are configured and valid.")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "One or more Control Plane endpoint URLs are invalid.", $"Invalid={string.Join(",", invalid)}"));
    }
}

public sealed class HelloRuntimeResourcesDiagnosticLevelTest(IHostEnvironment environment) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.HELLO.L4.RUNTIME.RESOURCES";
    public string Name => "Runtime resources observable";
    public string Owner => "Aegis.Hello";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = Path.GetPathRoot(environment.ContentRootPath) ?? environment.ContentRootPath;
            DriveInfo drive = new(root);
            long workingSet = Environment.WorkingSet;
            long managed = GC.GetTotalMemory(false);
            string evidence = $"Processors={Environment.ProcessorCount}; WorkingSetBytes={workingSet}; ManagedBytes={managed}; DiskFreeBytes={drive.AvailableFreeSpace}";
            if (drive.AvailableFreeSpace < 512L * 1024 * 1024)
                return Task.FromResult(EngineeringDiagnosticPolicy.Warning(TestId, Name, "Runtime is observable but disk free space is low.", evidence));

            return Task.FromResult(EngineeringDiagnosticPolicy.Passed(TestId, Name, "Runtime resources are observable.", evidence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(EngineeringDiagnosticPolicy.Warning(TestId, Name, "One or more runtime resource metrics could not be read.", $"FailureType={ex.GetType().Name}"));
        }
    }
}

public sealed class HelloVersionDiagnosticLevelTest : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.HELLO.L5.VERSION.KNOWN";
    public string Name => "Component version identifiable";
    public string Owner => "Aegis.Hello";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string version = typeof(HelloVersionDiagnosticLevelTest).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
            ?? typeof(HelloVersionDiagnosticLevelTest).Assembly.GetName().Version?.ToString()
            ?? "Unknown";

        return Task.FromResult(
            string.Equals(version, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? EngineeringDiagnosticPolicy.Warning(TestId, Name, "Component version is unknown.")
                : EngineeringDiagnosticPolicy.Passed(TestId, Name, "Component version is identifiable.", $"Version={version}"));
    }
}
