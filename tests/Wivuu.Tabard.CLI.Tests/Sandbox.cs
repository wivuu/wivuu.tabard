using System.Runtime.CompilerServices;

namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// Redirects the home directory the whole test run resolves against.
/// <see cref="Paths"/> reads it once into static properties, so this has to happen before any test
/// touches it - hence a module initializer rather than a TUnit hook. Everything the run creates
/// lives under one root, which goes away with the process.
/// </summary>
internal static class Sandbox
{
    private static string _root = "";

    /// <summary>Stands in for the user's home directory. Cleared between store tests.</summary>
    public static string Home { get; private set; } = "";

    [ModuleInitializer]
    public static void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"tabard-tests-{Environment.ProcessId}-{Guid.NewGuid():N}"
        );

        Home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Home);

        Environment.SetEnvironmentVariable("HOME", Home);
        Environment.SetEnvironmentVariable("USERPROFILE", Home);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(_root);
    }

    /// <summary>
    /// Empties the home directory so a test starts from nothing. Callers share one process and
    /// therefore one <see cref="Paths"/>, so tests that use this must not run in parallel.
    /// </summary>
    public static void Reset()
    {
        foreach (var entry in new DirectoryInfo(Home).EnumerateFileSystemInfos())
            Delete(entry.FullName);
    }

    /// <summary>
    /// A directory for tests that only need somewhere to put files. Kept outside <see cref="Home"/>
    /// so <see cref="Reset"/> cannot pull it out from under a test running alongside.
    /// </summary>
    public static string Scratch([CallerMemberName] string name = "")
    {
        var dir = Path.Combine(_root, "scratch", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Removes a path without following links out of it - the tests deliberately create links
    /// pointing at content they also assert survives.
    /// </summary>
    public static void Delete(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            if (info is DirectoryInfo directory && info.LinkTarget is null)
                directory.Delete(recursive: true);
            else
                info.Delete();
        }
        catch
        {
            // Leftovers in the temp directory are not worth failing a test over.
        }
    }
}
