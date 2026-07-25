namespace Wivuu.Tabard.Cli.Tests;

public class OptionsTests
{
    private const string Usage = "usage: tabard add <name>";

    [Test]
    public async Task Parse_takes_the_name()
    {
        var options = Parse("work");

        await Assert.That(options.Name).IsEqualTo("work");
        await Assert.That(options.UseOpenRouter).IsFalse();
        await Assert.That(options.Models).IsEmpty();
    }

    [Test]
    public async Task Parse_reads_the_flags_in_any_order()
    {
        var options = Parse("--openrouter", "work", "--key-stdin");

        await Assert.That(options.Name).IsEqualTo("work");
        await Assert.That(options.UseOpenRouter).IsTrue();
        await Assert.That(options.KeyFromStdin).IsTrue();
    }

    [Test]
    public async Task Parse_spreads_a_single_model_across_every_slot()
    {
        var options = Parse("work", "--model", "qwen/qwen3-coder");

        await Assert.That(options.Models.Count).IsEqualTo(OpenRouter.Slots.Length);
        await Assert.That(options.Models.Values.Distinct().Single()).IsEqualTo("qwen/qwen3-coder");
    }

    [Test]
    public async Task Parse_sets_one_tier_at_a_time()
    {
        var options = Parse("work", "--haiku", "anthropic/claude-haiku-4.5");

        await Assert
            .That(options.Models["ANTHROPIC_DEFAULT_HAIKU_MODEL"])
            .IsEqualTo("anthropic/claude-haiku-4.5");
        await Assert.That(options.Models.Count).IsEqualTo(1);
    }

    /// <summary>A later flag is the more specific instruction, so it wins.</summary>
    [Test]
    public async Task Parse_lets_a_tier_flag_override_the_blanket_one()
    {
        var options = Parse(
            "work",
            "--model",
            "openrouter/auto",
            "--opus",
            "anthropic/claude-opus-5"
        );

        await Assert
            .That(options.Models["ANTHROPIC_DEFAULT_OPUS_MODEL"])
            .IsEqualTo("anthropic/claude-opus-5");
        await Assert
            .That(options.Models["ANTHROPIC_DEFAULT_SONNET_MODEL"])
            .IsEqualTo("openrouter/auto");
    }

    /// <summary>There is nothing else a model slug or an API key could be configuring.</summary>
    [Test]
    [Arguments("--model", "auto")]
    [Arguments("--opus", "anthropic/claude-opus-5")]
    public async Task Parse_treats_a_model_flag_as_asking_for_openrouter(string flag, string slug)
    {
        await Assert.That(Parse("work", flag, slug).UseOpenRouter).IsTrue();
    }

    [Test]
    public async Task Parse_treats_key_stdin_as_asking_for_openrouter()
    {
        await Assert.That(Parse("work", "--key-stdin").UseOpenRouter).IsTrue();
    }

    [Test]
    [Arguments("auto", "openrouter/auto")]
    [Arguments("auto-beta", "openrouter/auto-beta")]
    [Arguments("qwen/qwen3-coder", "qwen/qwen3-coder")]
    public async Task Parse_expands_the_router_shorthands(string given, string expected)
    {
        await Assert
            .That(Parse("work", "--model", given).Models["ANTHROPIC_DEFAULT_OPUS_MODEL"])
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Parse_needs_a_name()
    {
        await Assert.That(() => Parse("--openrouter")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_rejects_a_second_name()
    {
        await Assert.That(() => Parse("work", "spare")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_rejects_an_unknown_option()
    {
        await Assert.That(() => Parse("work", "--nope")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_rejects_a_model_flag_with_no_slug()
    {
        await Assert.That(() => Parse("work", "--opus")).Throws<ArgumentException>();
    }

    /// <summary>A key in argv ends up in shell history, so the flag does not exist and says why.</summary>
    [Test]
    public async Task Parse_explains_why_there_is_no_key_flag()
    {
        await Assert
            .That(() => Parse("work", "--key", "sk-or-v1-abc"))
            .Throws<ArgumentException>()
            .WithMessageContaining("--key-stdin");
    }

    private static AddOptions Parse(params string[] args) => AddOptions.Parse(args, Usage);
}
