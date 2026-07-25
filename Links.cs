using System.Diagnostics;

namespace Envy.Cli;

internal static class Links
{
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

    /// <summary>True if the path exists and is a reparse point / symlink rather than real content.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool PointsAt(string linkPath, string target)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(linkPath) ? new DirectoryInfo(linkPath) : new FileInfo(linkPath);
            if (info.LinkTarget is not { } t)
                return false;

            var resolved = Path.IsPathRooted(t) ? t : Path.Combine(Path.GetDirectoryName(linkPath) ?? "", t);
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
