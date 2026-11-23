namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// PATH cut down to the system's own folders, so which sandboxes count as installed is up to the
/// test - through <see cref="FakeSrt"/> and <see cref="FakeSbx"/> made after it - rather than to
/// whatever the machine running the tests has. PATH belongs to the whole process, so tests that use
/// this must not run in parallel. Leaves PATH alone on Windows, where no sandbox runs.
/// </summary>
internal sealed class OnlyOnPath : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public OnlyOnPath()
    {
        if (!OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("PATH", "/usr/bin:/bin");
    }

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _originalPath);
}
