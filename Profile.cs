using System.Text.Json;

namespace Envy.Cli;

internal sealed class Profile
{
    public required string Name { get; init; }

    /// <summary>The directory handed to Claude Code as CLAUDE_CONFIG_DIR.</summary>
    public required string Dir { get; init; }

    public string? Account { get; private set; }
    public string? Plan { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public string CredentialsFile => Path.Combine(Dir, ".credentials.json");
    public string ClaudeJsonFile => Path.Combine(Dir, ".claude.json");

    /// <summary>
    /// Reads whatever we can recognise for display. Nothing here is contractual - the on-disk
    /// shape belongs to Claude Code and can change - so every probe fails soft to null.
    /// </summary>
    public void ReadMetadata()
    {
        ReadCredentials();
        ReadAccount();
    }

    private void ReadCredentials()
    {
        if (!File.Exists(CredentialsFile))
            return;

        try
        {
            using var stream = File.OpenRead(CredentialsFile);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return;

            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var epochMs))
                ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

            if (oauth.TryGetProperty("subscriptionType", out var sub) && sub.ValueKind == JsonValueKind.String)
                Plan = sub.GetString();
        }
        catch
        {
            // Unreadable or unexpected shape - show nothing rather than guess.
        }
    }

    private void ReadAccount()
    {
        if (!File.Exists(ClaudeJsonFile))
            return;

        try
        {
            using var stream = File.OpenRead(ClaudeJsonFile);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("oauthAccount", out var account)
                && account.TryGetProperty("emailAddress", out var email)
                && email.ValueKind == JsonValueKind.String)
            {
                Account = email.GetString();
            }
        }
        catch
        {
            // Same as above.
        }
    }

    /// <summary>A one-line summary for the picker.</summary>
    public string Describe()
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(Account))
            parts.Add(Account);

        if (!string.IsNullOrWhiteSpace(Plan))
            parts.Add(Plan);

        parts.Add(ExpiresAt switch
        {
            // On macOS credentials live in the Keychain, so an empty file here is normal.
            null => "no token file",
            {} e when e <= DateTimeOffset.UtcNow => "refresh due",
            {} e => $"valid {FormatSpan(e - DateTimeOffset.UtcNow)}",
        });

        return string.Join("  -  ", parts);
    }

    private static string FormatSpan(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h",
        _ => $"{Math.Max(1, (int)span.TotalMinutes)}m",
    };
}
