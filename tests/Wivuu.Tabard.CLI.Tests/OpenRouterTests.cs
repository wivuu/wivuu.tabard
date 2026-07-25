using System.Text;

namespace Wivuu.Tabard.Cli.Tests;

public class OpenRouterTests
{
    /// <summary>
    /// Shaped like the real /v1/models: a tool-capable model, one without tools, an alias and a
    /// router that declare no parameters at all, and an entry with nothing usable in it.
    /// </summary>
    private const string Catalog = """
        {
          "data": [
            {
              "id": "qwen/qwen3-coder",
              "name": "Qwen3 Coder",
              "context_length": 262144,
              "pricing": { "prompt": "0.0000003", "completion": "0.000001" },
              "supported_parameters": ["max_tokens", "tools", "tool_choice"]
            },
            {
              "id": "some/completion-only",
              "name": "No Tools Here",
              "context_length": 8192,
              "pricing": { "prompt": "0.000001", "completion": "0.000002" },
              "supported_parameters": ["max_tokens"]
            },
            {
              "id": "~anthropic/claude-fable-latest",
              "name": "Claude Fable (latest)",
              "context_length": 200000
            },
            {
              "id": "openrouter/auto",
              "name": "Auto Router"
            },
            { "name": "no id at all" }
          ]
        }
        """;

    [Test]
    public async Task ParseCatalog_keeps_tool_capable_models()
    {
        await Assert.That(Ids()).Contains("qwen/qwen3-coder");
    }

    /// <summary>Claude Code cannot work without tool calls, so these are noise in the list.</summary>
    [Test]
    public async Task ParseCatalog_drops_models_that_cannot_call_tools()
    {
        await Assert.That(Ids()).DoesNotContain("some/completion-only");
    }

    /// <summary>
    /// The aliases and the routers declare no parameters - filtering on 'tools' at the API would
    /// hide the very models the wizard defaults to.
    /// </summary>
    [Test]
    [Arguments("~anthropic/claude-fable-latest")]
    [Arguments("openrouter/auto")]
    public async Task ParseCatalog_keeps_aliases_and_routers(string id)
    {
        await Assert.That(Ids()).Contains(id);
    }

    [Test]
    public async Task ParseCatalog_skips_entries_without_an_id()
    {
        await Assert.That(Parse().Count).IsEqualTo(3);
    }

    /// <summary>Routers first, then the floating aliases, then everything else.</summary>
    [Test]
    public async Task ParseCatalog_puts_routers_and_aliases_first()
    {
        await Assert
            .That(string.Join(",", Ids()))
            .IsEqualTo("openrouter/auto,~anthropic/claude-fable-latest,qwen/qwen3-coder");
    }

    [Test]
    public async Task ParseCatalog_converts_prices_to_millions_of_tokens()
    {
        var model = Parse().First(m => m.Id == "qwen/qwen3-coder");

        await Assert.That(model.PromptPrice!.Value).IsEqualTo(0.3).Within(0.0001);
        await Assert.That(model.CompletionPrice!.Value).IsEqualTo(1).Within(0.0001);
        await Assert.That(model.Summary).IsEqualTo("262.1K ctx   $0.3/$1 per Mtok");
    }

    [Test]
    public async Task ParseCatalog_leaves_prices_null_when_there_are_none()
    {
        var model = Parse().First(m => m.Id == OpenRouter.Auto);

        await Assert.That(model.PromptPrice).IsNull();
        await Assert.That(model.Summary).IsEqualTo("");
    }

    [Test]
    public async Task Configures_recognises_an_openrouter_base_url()
    {
        var env = new Dictionary<string, string>
        {
            [OpenRouter.BaseUrlVariable] = "https://openrouter.ai/api",
        };

        await Assert.That(OpenRouter.Configures(env)).IsTrue();
    }

    [Test]
    [Arguments("https://api.anthropic.com")]
    public async Task Configures_ignores_any_other_base_url(string url)
    {
        var env = new Dictionary<string, string> { [OpenRouter.BaseUrlVariable] = url };

        await Assert.That(OpenRouter.Configures(env)).IsFalse();
    }

    [Test]
    public async Task Configures_is_false_without_a_base_url()
    {
        await Assert.That(OpenRouter.Configures(new Dictionary<string, string>())).IsFalse();
    }

    /// <summary>An empty ANTHROPIC_API_KEY is what stops Claude Code trying its own auth first.</summary>
    [Test]
    public async Task Configure_blanks_the_api_key_and_sets_the_base_url()
    {
        var values = OpenRouter.Configure("sk-or-v1-abc").ToDictionary(v => v.Key, v => v.Value);

        await Assert.That(values[OpenRouter.BaseUrlVariable]).IsEqualTo(OpenRouter.BaseUrl);
        await Assert.That(values[OpenRouter.ApiKeyVariable]).IsEqualTo("");
        await Assert.That(values[OpenRouter.AuthTokenVariable]).IsEqualTo("sk-or-v1-abc");
    }

    [Test]
    public async Task Configure_leaves_the_key_alone_when_there_is_not_one()
    {
        var values = OpenRouter.Configure(null).ToDictionary(v => v.Key, v => v.Value);

        await Assert.That(values.ContainsKey(OpenRouter.AuthTokenVariable)).IsFalse();
    }

    [Test]
    public async Task Configure_writes_only_the_slots_it_is_given()
    {
        var models = new Dictionary<string, string>
        {
            [OpenRouter.Slots[0].Variable] = "anthropic/claude-opus-5",
        };

        var values = OpenRouter.Configure(null, models).ToDictionary(v => v.Key, v => v.Value);

        await Assert
            .That(values[OpenRouter.Slots[0].Variable])
            .IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(values.ContainsKey(OpenRouter.Slots[1].Variable)).IsFalse();
    }

    /// <summary>Claude Code 2.1.220 reads all five; older ones ignore what they do not know.</summary>
    [Test]
    public async Task Slots_cover_every_model_tier()
    {
        await Assert
            .That(string.Join(",", OpenRouter.Slots.Select(s => s.Label)))
            .IsEqualTo("opus,sonnet,haiku,fable,subagent");
    }

    [Test]
    [Arguments("sk-or-v1-0123456789abcdef", "sk-or-v1-...cdef")]
    [Arguments("short", "*****")]
    public async Task Redact_keeps_only_the_ends(string key, string expected)
    {
        await Assert.That(OpenRouter.Redact(key)).IsEqualTo(expected);
    }

    [Test]
    public async Task ParseKey_reads_the_label_and_the_credit_left()
    {
        var check = OpenRouter.ParseKey(
            Stream("""{"data":{"label":"laptop","limit_remaining":12.5,"is_free_tier":false}}""")
        );

        await Assert.That(check.Status).IsEqualTo(KeyStatus.Valid);
        await Assert.That(check.Label).IsEqualTo("laptop");
        await Assert.That(check.Remaining).IsEqualTo(12.5);
        await Assert.That(check.FreeTier).IsFalse();
    }

    /// <summary>The 200 is the answer that matters; the body is decoration.</summary>
    [Test]
    public async Task ParseKey_still_says_valid_when_the_body_makes_no_sense()
    {
        var check = OpenRouter.ParseKey(Stream("not json"));

        await Assert.That(check.Status).IsEqualTo(KeyStatus.Valid);
        await Assert.That(check.Label).IsNull();
    }

    private static List<Model> Parse() => OpenRouter.ParseCatalog(Stream(Catalog));

    private static List<string> Ids() => [.. Parse().Select(m => m.Id)];

    private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json));
}
