using System.Text.Json;

namespace Wivuu.Tabard.Cli;

internal sealed class Profile
{
    private bool _read;

    public required string Name { get; init; }

    /// <summary>The directory handed to Claude Code as CLAUDE_CONFIG_DIR.</summary>
    public required string Dir { get; init; }

    public string? Account { get; private set; }
    public string? Plan { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public string CredentialsFile => Path.Combine(Dir, ".credentials.json");
    public string ClaudeJsonFile => Path.Combine(Dir, ".claude.json");

    /// <summary>
    /// Reads whatever we can recognise for display, once and only when something is about to show
    /// it. Nothing here is contractual - the on-disk shape belongs to Claude Code and can change -
    /// so every probe fails soft to null.
    /// </summary>
    private void ReadMetadata()
    {
        if (_read)
            return;

        _read = true;
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

            if (
                oauth.TryGetProperty("subscriptionType", out var sub)
                && sub.ValueKind == JsonValueKind.String
            )
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
            Account = FindEmail(stream);
        }
        catch
        {
            // Same as above.
        }
    }

    /// <summary>
    /// Scans for oauthAccount.emailAddress a chunk at a time. This file carries every project a
    /// machine has ever opened and reaches tens of megabytes, so parsing it into a document to
    /// pull out one string costs more than the whole rest of the program.
    /// </summary>
    private static string? FindEmail(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        var state = new JsonReaderState();
        var cursor = default(Cursor);
        var filled = 0;

        while (true)
        {
            // Only grows when a single token spans the whole buffer, which the tokens we care
            // about never do.
            if (filled == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            var read = stream.Read(buffer.AsSpan(filled));
            var final = read == 0;
            filled += read;

            var reader = new Utf8JsonReader(buffer.AsSpan(0, filled), final, state);
            var email = Scan(ref reader, ref cursor);

            if (email is not null || cursor.Done || final)
                return email;

            state = reader.CurrentState;

            var consumed = (int)reader.BytesConsumed;
            buffer.AsSpan(consumed, filled - consumed).CopyTo(buffer);
            filled -= consumed;
        }
    }

    /// <summary>Everything the scan has to remember across chunk boundaries.</summary>
    private struct Cursor
    {
        public bool Entering;
        public bool InAccount;
        public int AccountDepth;
        public bool WantEmail;
        public bool Done;
    }

    private static string? Scan(ref Utf8JsonReader reader, ref Cursor cursor)
    {
        while (!cursor.Done && reader.Read())
        {
            // Only trust an oauthAccount that is actually an object, so a stray 'emailAddress'
            // nested somewhere else cannot be mistaken for the account's.
            if (cursor.Entering)
            {
                cursor.Entering = false;
                cursor.InAccount = reader.TokenType == JsonTokenType.StartObject;
                cursor.Done = !cursor.InAccount;
                cursor.AccountDepth = reader.CurrentDepth;
                continue;
            }

            if (cursor.WantEmail)
            {
                cursor.Done = true;
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName
                    when !cursor.InAccount && reader.ValueTextEquals("oauthAccount"u8):
                    cursor.Entering = true;
                    break;

                case JsonTokenType.PropertyName
                    when cursor.InAccount
                        && reader.CurrentDepth == cursor.AccountDepth + 1
                        && reader.ValueTextEquals("emailAddress"u8):
                    cursor.WantEmail = true;
                    break;

                // Left the object without finding it - there is nothing more to look for.
                case JsonTokenType.EndObject
                    when cursor.InAccount && reader.CurrentDepth == cursor.AccountDepth:
                    cursor.Done = true;
                    break;
            }
        }

        return null;
    }

    /// <summary>A one-line summary for the picker.</summary>
    public string Describe()
    {
        ReadMetadata();

        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(Account))
            parts.Add(Account);

        if (!string.IsNullOrWhiteSpace(Plan))
            parts.Add(Plan);

        parts.Add(
            ExpiresAt switch
            {
                // On macOS credentials live in the Keychain, so an empty file here is normal.
                null => "no token file",
                { } e when e <= DateTimeOffset.UtcNow => "refresh due",
                { } e => $"valid {FormatSpan(e - DateTimeOffset.UtcNow)}",
            }
        );

        return string.Join("  -  ", parts);
    }

    private static string FormatSpan(TimeSpan span) =>
        span switch
        {
            { TotalDays: >= 1 } => $"{(int)span.TotalDays}d",
            { TotalHours: >= 1 } => $"{(int)span.TotalHours}h",
            _ => $"{Math.Max(1, (int)span.TotalMinutes)}m",
        };
}
