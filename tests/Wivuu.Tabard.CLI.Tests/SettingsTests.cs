using System.Text.Json;

namespace Wivuu.Tabard.Cli.Tests;

public class SettingsTests
{
    [Test]
    public async Task ReadEnv_is_empty_when_there_is_no_settings_file()
    {
        await Assert.That(Settings.ReadEnv(Sandbox.Scratch())).IsEmpty();
    }

    [Test]
    public async Task ReadEnv_reads_the_env_block()
    {
        var dir = Sandbox.Scratch();
        Write(dir, """{"env":{"ANTHROPIC_BASE_URL":"https://openrouter.ai/api"}}""");

        await Assert
            .That(Settings.ReadEnv(dir)["ANTHROPIC_BASE_URL"])
            .IsEqualTo("https://openrouter.ai/api");
    }

    /// <summary>The file belongs to Claude Code; anything unexpected in it configures nothing.</summary>
    [Test]
    [Arguments("not json at all")]
    [Arguments("[]")]
    [Arguments("""{"env":"a string"}""")]
    [Arguments("""{"env":{"ANTHROPIC_BASE_URL":42}}""")]
    public async Task ReadEnv_fails_soft_on_anything_unexpected(string content)
    {
        var dir = Sandbox.Scratch();
        Write(dir, content);

        await Assert.That(Settings.ReadEnv(dir)).IsEmpty();
    }

    [Test]
    public async Task MergeEnv_creates_the_file_when_there_is_none()
    {
        var dir = Sandbox.Scratch();

        Settings.MergeEnv(dir, [new("ANTHROPIC_AUTH_TOKEN", "sk-or-v1-abc")]);

        await Assert.That(Settings.ReadEnv(dir)["ANTHROPIC_AUTH_TOKEN"]).IsEqualTo("sk-or-v1-abc");
    }

    /// <summary>
    /// The whole design rests on this: tabard writes into a file Claude Code also owns, so anything
    /// it did not put there has to come back out untouched.
    /// </summary>
    [Test]
    public async Task MergeEnv_preserves_keys_it_does_not_own()
    {
        var dir = Sandbox.Scratch();
        Write(
            dir,
            """
            {
              "model": "opus",
              "permissions": { "allow": ["Bash(ls:*)"] },
              "env": { "SOMETHING_ELSE": "keep me" }
            }
            """
        );

        Settings.MergeEnv(dir, [new("ANTHROPIC_API_KEY", "")]);

        using var doc = JsonDocument.Parse(File.ReadAllText(Settings.FileFor(dir)));
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("model").GetString()).IsEqualTo("opus");
        await Assert
            .That(root.GetProperty("permissions").GetProperty("allow")[0].GetString())
            .IsEqualTo("Bash(ls:*)");

        var env = Settings.ReadEnv(dir);
        await Assert.That(env["SOMETHING_ELSE"]).IsEqualTo("keep me");
        await Assert.That(env["ANTHROPIC_API_KEY"]).IsEqualTo("");
    }

    [Test]
    public async Task MergeEnv_replaces_a_value_in_place_rather_than_duplicating_it()
    {
        var dir = Sandbox.Scratch();
        Write(dir, """{"env":{"ANTHROPIC_AUTH_TOKEN":"old","OTHER":"x"}}""");

        Settings.MergeEnv(dir, [new("ANTHROPIC_AUTH_TOKEN", "new")]);

        var text = File.ReadAllText(Settings.FileFor(dir));
        await Assert.That(Settings.ReadEnv(dir)["ANTHROPIC_AUTH_TOKEN"]).IsEqualTo("new");
        await Assert.That(text.Split("ANTHROPIC_AUTH_TOKEN").Length).IsEqualTo(2);
    }

    [Test]
    public async Task MergeEnv_removes_a_key_given_a_null_value()
    {
        var dir = Sandbox.Scratch();
        Write(dir, """{"env":{"ANTHROPIC_AUTH_TOKEN":"old","OTHER":"x"}}""");

        Settings.MergeEnv(dir, [new("ANTHROPIC_AUTH_TOKEN", null)]);

        var env = Settings.ReadEnv(dir);
        await Assert.That(env.ContainsKey("ANTHROPIC_AUTH_TOKEN")).IsFalse();
        await Assert.That(env["OTHER"]).IsEqualTo("x");
    }

    /// <summary>Broken JSON is still the user's content, so it is moved aside rather than lost.</summary>
    [Test]
    public async Task MergeEnv_sets_an_unparseable_file_aside()
    {
        var dir = Sandbox.Scratch();
        Write(dir, "{ this is not json");

        var warnings = Settings.MergeEnv(dir, [new("ANTHROPIC_API_KEY", "")]);

        await Assert.That(warnings).IsNotEmpty();
        await Assert.That(File.Exists(Settings.FileFor(dir) + ".broken")).IsTrue();
        await Assert.That(Settings.ReadEnv(dir)).ContainsKey("ANTHROPIC_API_KEY");
    }

    /// <summary>It holds an API key, so it must not be readable by anyone else.</summary>
    [Test]
    public async Task MergeEnv_writes_a_private_file()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dir = Sandbox.Scratch();
        Settings.MergeEnv(dir, [new("ANTHROPIC_AUTH_TOKEN", "sk-or-v1-abc")]);

        var mode = File.GetUnixFileMode(Settings.FileFor(dir));

        await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void Write(string dir, string content) =>
        File.WriteAllText(Settings.FileFor(dir), content);
}
