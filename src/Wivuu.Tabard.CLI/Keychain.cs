using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Claude Code's login on macOS lives in the login Keychain rather than in .credentials.json: a
/// generic password whose service is 'Claude Code-credentials', with '-' and the first eight hex
/// characters of the SHA-256 of CLAUDE_CONFIG_DIR appended when that is set, and whose account is
/// your user name. tabard always sets CLAUDE_CONFIG_DIR, so each profile has its own item.
///
/// Everything goes through /usr/bin/security, which is how Claude Code itself reaches it - so its
/// items let 'security' read them without asking. The secret never reaches a command line: it is
/// read from stdout, and written back as hex on the stdin of 'security -i'.
/// </summary>
internal static class Keychain
{
    private const string Security = "/usr/bin/security";
    private const string ServicePrefix = "Claude Code-credentials";

    /// <summary>The service a profile directory's login is saved under. <paramref name="home"/>
    /// is the unset-variable case: a bare 'claude' using ~/.claude.</summary>
    public static string ServiceFor(string configDir, bool home = false)
    {
        if (home)
            return ServicePrefix;

        var path = Path.GetFullPath(configDir);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
        return $"{ServicePrefix}-{hash}";
    }

    /// <summary>
    /// Copies the login saved for <paramref name="fromDir"/> to <paramref name="toDir"/>. Does
    /// nothing off macOS, or when there is no login to copy - .credentials.json covers those.
    /// Returns a problem to report, or null.
    /// </summary>
    public static string? CopyLogin(string fromDir, string toDir, bool sourceIsHome)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(Security))
            return null;

        try
        {
            var account = Environment.UserName;

            // An adopted ~/.claude was last logged in to by a bare 'claude', so its login may still
            // be under the plain service rather than the profile's own.
            var secret = Read(ServiceFor(fromDir, sourceIsHome), account);
            if (secret is null && !sourceIsHome && IsLinkedHome(fromDir))
                secret = Read(ServiceFor(fromDir, home: true), account);

            if (secret is null)
                return null;

            return Write(ServiceFor(toDir), account, secret)
                ? null
                : "could not save the copied login to the Keychain - log in with /login.";
        }
        catch (Exception ex)
        {
            return $"could not copy the Keychain login ({ex.Message}) - log in with /login.";
        }
    }

    /// <summary>Whether ~/.claude links to <paramref name="dir"/>, so a bare 'claude' run
    /// there wrote its login under the plain service.</summary>
    private static bool IsLinkedHome(string dir) => Links.PointsAt(Paths.ClaudeDir, dir);

    private static string? Read(string service, string account)
    {
        var psi = new ProcessStartInfo(Security)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in new[] { "find-generic-password", "-a", account, "-s", service, "-w" })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null)
            return null;

        var stderr = proc.StandardError.ReadToEndAsync();
        var secret = proc.StandardOutput.ReadToEnd().TrimEnd('\n');

        if (!proc.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            proc.Kill(entireProcessTree: true);
            return null;
        }

        _ = stderr.Result;
        return proc.ExitCode == 0 && secret.Length > 0 ? secret : null;
    }

    private static bool Write(string service, string account, string secret)
    {
        // Names are quoted for security's own parser; neither can hold a quote here - the service
        // is ours, and a user name with one in it is refused rather than mangled.
        if (account.Contains('"') || account.Contains('\n'))
            return false;

        var psi = new ProcessStartInfo(Security)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("-i");

        using var proc = Process.Start(psi);
        if (proc is null)
            return false;

        var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(secret));
        proc.StandardInput.Write(
            $"add-generic-password -U -a \"{account}\" -s \"{service}\" -X {hex}\n"
        );
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            proc.Kill(entireProcessTree: true);
            return false;
        }

        _ = stdout.Result;
        var errors = stderr.Result;

        // 'security -i' exits 0 whatever its commands did, so its complaints are the signal.
        return proc.ExitCode == 0 && errors.Trim().Length == 0;
    }
}
