using System.Text;

namespace Wivuu.Tabard.Cli.Tests;

public class ProfileTests
{
    [Test]
    public async Task Files_are_derived_from_the_profile_directory()
    {
        var dir = Sandbox.Scratch();
        var profile = At(dir);

        await Assert
            .That(profile.CredentialsFile)
            .IsEqualTo(Path.Combine(dir, ".credentials.json"));
        await Assert.That(profile.ClaudeJsonFile).IsEqualTo(Path.Combine(dir, ".claude.json"));
    }

    /// <summary>On macOS credentials live in the Keychain, so nothing on disk is the normal case.</summary>
    [Test]
    public async Task Describe_says_so_when_there_is_no_token_file()
    {
        await Assert.That(At(Sandbox.Scratch()).Describe()).IsEqualTo("no token file");
    }

    [Test]
    public async Task Describe_reports_an_expired_token_as_due_for_refresh()
    {
        var dir = Sandbox.Scratch();
        WriteCredentials(dir, DateTimeOffset.UtcNow.AddHours(-1));

        await Assert.That(At(dir).Describe()).IsEqualTo("refresh due");
    }

    // Half-unit offsets, so a slow test cannot round the answer down a step.
    [Test]
    [Arguments(3 * 86400 + 43200, "valid 3d")]
    [Arguments(5 * 3600 + 1800, "valid 5h")]
    [Arguments(150, "valid 2m")]
    public async Task Describe_scales_the_remaining_time(int seconds, string expected)
    {
        var dir = Sandbox.Scratch();
        WriteCredentials(dir, DateTimeOffset.UtcNow.AddSeconds(seconds));

        await Assert.That(At(dir).Describe()).IsEqualTo(expected);
    }

    /// <summary>Under a minute still reads as a minute rather than '0m'.</summary>
    [Test]
    public async Task Describe_rounds_the_last_seconds_up()
    {
        var dir = Sandbox.Scratch();
        WriteCredentials(dir, DateTimeOffset.UtcNow.AddSeconds(20));

        await Assert.That(At(dir).Describe()).IsEqualTo("valid 1m");
    }

    [Test]
    public async Task Describe_joins_account_plan_and_expiry()
    {
        var dir = Sandbox.Scratch();
        WriteCredentials(dir, DateTimeOffset.UtcNow.AddSeconds(4 * 86400 + 43200), plan: "max");
        WriteClaudeJson(dir, """{"oauthAccount":{"emailAddress":"me@example.com"}}""");

        await Assert.That(At(dir).Describe()).IsEqualTo("me@example.com  -  max  -  valid 4d");
    }

    [Test]
    [Arguments("not json at all")]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"claudeAiOauth":"wrong shape"}""")]
    [Arguments("""{"claudeAiOauth":{"expiresAt":"not a number"}}""")]
    public async Task Describe_falls_back_when_the_credentials_make_no_sense(string content)
    {
        var dir = Sandbox.Scratch();
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), content);

        await Assert.That(At(dir).Describe()).IsEqualTo("no token file");
    }

    [Test]
    public async Task Account_comes_from_oauthAccount()
    {
        var dir = Sandbox.Scratch();
        WriteClaudeJson(
            dir,
            """{"numStartups":7,"oauthAccount":{"accountUuid":"abc","emailAddress":"me@example.com"}}"""
        );

        await Assert.That(At(dir).Describe()).IsEqualTo("me@example.com  -  no token file");
    }

    /// <summary>An emailAddress belonging to something else is not the account's.</summary>
    [Test]
    [Arguments("""{"projects":{"emailAddress":"wrong@example.com"}}""")]
    [Arguments("""{"oauthAccount":"not-an-object","emailAddress":"wrong@example.com"}""")]
    [Arguments("""{"oauthAccount":{"nested":{"emailAddress":"wrong@example.com"}}}""")]
    [Arguments("""{"oauthAccount":{"emailAddress":42}}""")]
    [Arguments("""{"oauthAccount":{"accountUuid":"abc"}}""")]
    [Arguments("truncated {\"oauthAccount\":")]
    public async Task Account_is_left_out_when_nothing_trustworthy_is_there(string content)
    {
        var dir = Sandbox.Scratch();
        WriteClaudeJson(dir, content);

        await Assert.That(At(dir).Describe()).IsEqualTo("no token file");
    }

    /// <summary>
    /// The real file reaches tens of megabytes, so the scan works a 16KB chunk at a time. Push the
    /// account past several refills to exercise the state carried across the boundaries.
    /// </summary>
    [Test]
    public async Task Account_is_found_past_the_end_of_the_first_chunk()
    {
        var dir = Sandbox.Scratch();
        var padding = new StringBuilder();

        for (var i = 0; i < 4000; i++)
            padding.Append($"\"project-{i}\":{{\"lastCost\":{i}.5,\"history\":[]}},");

        WriteClaudeJson(
            dir,
            "{\"projects\":{"
                + padding
                + "\"final\":{}},\"oauthAccount\":{\"emailAddress\":\"me@example.com\"}}"
        );

        var size = new FileInfo(Path.Combine(dir, ".claude.json")).Length;
        await Assert.That(size).IsGreaterThan(16 * 1024);
        await Assert.That(At(dir).Describe()).IsEqualTo("me@example.com  -  no token file");
    }

    /// <summary>A single token longer than the whole buffer is the one case that grows it.</summary>
    [Test]
    public async Task Account_is_found_after_a_token_larger_than_the_buffer()
    {
        var dir = Sandbox.Scratch();
        var huge = new string('x', 64 * 1024);

        WriteClaudeJson(
            dir,
            "{\"note\":\"" + huge + "\",\"oauthAccount\":{\"emailAddress\":\"me@example.com\"}}"
        );

        await Assert.That(At(dir).Describe()).IsEqualTo("me@example.com  -  no token file");
    }

    /// <summary>Reading is deferred until something is about to display it, then cached.</summary>
    [Test]
    public async Task Metadata_is_read_once()
    {
        var dir = Sandbox.Scratch();
        WriteClaudeJson(dir, """{"oauthAccount":{"emailAddress":"first@example.com"}}""");

        var profile = At(dir);
        await Assert.That(profile.Describe()).IsEqualTo("first@example.com  -  no token file");

        WriteClaudeJson(dir, """{"oauthAccount":{"emailAddress":"second@example.com"}}""");
        await Assert.That(profile.Describe()).IsEqualTo("first@example.com  -  no token file");
    }

    private static Profile At(string dir) => new() { Name = Path.GetFileName(dir), Dir = dir };

    private static void WriteCredentials(string dir, DateTimeOffset expiresAt, string? plan = null)
    {
        var subscription = plan is null ? "" : $",\"subscriptionType\":\"{plan}\"";
        File.WriteAllText(
            Path.Combine(dir, ".credentials.json"),
            "{\"claudeAiOauth\":{\"expiresAt\":"
                + expiresAt.ToUnixTimeMilliseconds()
                + subscription
                + "}}"
        );
    }

    private static void WriteClaudeJson(string dir, string content) =>
        File.WriteAllText(Path.Combine(dir, ".claude.json"), content);
}
