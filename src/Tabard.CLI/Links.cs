using System.Diagnostics;

namespace Tabard.Cli;

internal static class Links
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Links <paramref name="linkPath"/> at <paramref name="target"/>. On Windows a real symlink
    /// needs Developer Mode or elevation, so we fall back to a directory junction, which does not.
    /// </summary>
    public static bool TryCreateDirectoryLink(string linkPath, string target, out string error)
    {
        error = "";
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows())
        {
            if (TryCreateJunction(linkPath, target))
                return true;

            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryCreateFileLink(string linkPath, string target, out string error)
    {
        error = "";
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex)
        {
            // Windows has no junction equivalent for files, so this is genuinely
            // unavailable without Developer Mode. Caller degrades gracefully.
            error = ex.Message;
            return false;
        }
    }

    /// <summary>True if the path is a reparse point / symlink rather than real content.</summary>
    public static bool IsLink(string path) => RawTarget(path) is not null;

    /// <summary>
    /// The absolute path a link points at, or null if it is not a link. Does not follow chains and
    /// does not require the target to exist - a dangling link still has a target.
    /// </summary>
    public static string? ResolveTarget(string linkPath)
    {
        if (RawTarget(linkPath) is not { } target)
            return null;

        try
        {
            var rooted = Path.IsPathRooted(target)
                ? target
                : Path.Combine(Path.GetDirectoryName(linkPath) ?? "", target);

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rooted));
        }
        catch
        {
            return null;
        }
    }

    public static bool PointsAt(string linkPath, string target)
    {
        if (ResolveTarget(linkPath) is not { } resolved)
            return false;

        try
        {
            return string.Equals(
                resolved,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
                PathComparison
            );
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="root"/> or sits under it.</summary>
    public static bool IsInside(string path, string root)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return path.Equals(full, PathComparison)
                || path.StartsWith(full + Path.DirectorySeparatorChar, PathComparison);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes a link without touching what it points at.</summary>
    public static void Unlink(string path)
    {
        try
        {
            if (!IsLink(path))
                return;

            // Never pass recursive:true here - that would delete through the link.
            if (Directory.Exists(path))
                Directory.Delete(path);
            else
                File.Delete(path);
        }
        catch
        {
            // A stuck link is not worth aborting the operation over.
        }
    }

    /// <summary>
    /// The target exactly as recorded on disk, or null if the path is not a link. Deliberately
    /// avoids FileSystemInfo.Exists: for a dangling link that is platform-dependent, and tabard
    /// creates dangling links on purpose (~/.claude.json before Claude Code has written it).
    /// </summary>
    private static string? RawTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return info.LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(linkPath);
            psi.ArgumentList.Add(target);

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit();
            return proc.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }
}
