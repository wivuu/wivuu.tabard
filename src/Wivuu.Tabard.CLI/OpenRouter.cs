using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>One model as the picker shows it. Prices are per million tokens, null when unknown.</summary>
internal sealed record Model(
    string Id,
    string Name,
    long ContextLength,
    double? PromptPrice,
    double? CompletionPrice
)
{
    /// <summary>The right-hand column: context window and what it costs.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(2);

            if (ContextLength > 0)
                parts.Add($"{FormatCount(ContextLength)} ctx");

            if (PromptPrice is { } prompt && CompletionPrice is { } completion)
                parts.Add($"${FormatPrice(prompt)}/${FormatPrice(completion)} per Mtok");
            else if (PromptPrice is { } only)
                parts.Add($"${FormatPrice(only)} per Mtok");

            return string.Join("   ", parts);
        }
    }

    private static string FormatCount(long tokens) =>
        tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
            >= 1_000 => $"{tokens / 1_000d:0.#}K",
            _ => tokens.ToString(CultureInfo.InvariantCulture),
        };

    private static string FormatPrice(double price) =>
        price switch
        {
            0 => "0",
            < 1 => price.ToString("0.###", CultureInfo.InvariantCulture),
            _ => price.ToString("0.##", CultureInfo.InvariantCulture),
        };
}

internal enum KeyStatus
{
    Valid,
    Rejected,
    Unknown,
}

internal sealed record KeyCheck(
    KeyStatus Status,
    string? Label = null,
    double? Remaining = null,
    bool FreeTier = false,
    string? Error = null
);

/// <summary>The catalog, plus whether it came from OpenRouter or from the built-in fallback.</summary>
internal sealed record Catalog(IReadOnlyList<Model> Models, bool Live, string? Error = null);

/// <summary>
/// Everything tabard knows about OpenRouter. Claude Code needs no plugin to talk to it - the whole
/// integration is environment variables, which is why an OpenRouter profile is just a profile with a
/// settings.json.
/// </summary>
internal static class OpenRouter
{
    public const string BaseUrl = "https://openrouter.ai/api";
    public const string ModelsUrl = "https://openrouter.ai/api/v1/models";
    public const string KeyUrl = "https://openrouter.ai/api/v1/key";
    public const string KeyEnvironmentVariable = "OPENROUTER_API_KEY";

    public const string BaseUrlVariable = "ANTHROPIC_BASE_URL";
    public const string AuthTokenVariable = "ANTHROPIC_AUTH_TOKEN";
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    public const string Auto = "openrouter/auto";
    public const string AutoBeta = "openrouter/auto-beta";

    /// <summary>
    /// One of Claude Code's model tiers and the OpenRouter slug it defaults to. The defaults are
    /// OpenRouter's floating '~vendor/model-latest' aliases, so '/model opus' keeps meaning the
    /// current Opus rather than pinning a version that ages out.
    /// </summary>
    public sealed record Slot(string Variable, string Flag, string Label, string Default);

    /// <summary>
    /// FABLE is honoured by Claude Code 2.1.220 alongside the older three; on a version that does not
    /// know the tier the variable is simply unread, so setting it costs nothing.
    /// </summary>
    public static readonly Slot[] Slots =
    [
        new("ANTHROPIC_DEFAULT_OPUS_MODEL", "--opus", "opus", "~anthropic/claude-opus-latest"),
        new(
            "ANTHROPIC_DEFAULT_SONNET_MODEL",
            "--sonnet",
            "sonnet",
            "~anthropic/claude-sonnet-latest"
        ),
        new("ANTHROPIC_DEFAULT_HAIKU_MODEL", "--haiku", "haiku", "~anthropic/claude-haiku-latest"),
        new("ANTHROPIC_DEFAULT_FABLE_MODEL", "--fable", "fable", "~anthropic/claude-fable-latest"),
        new(
            "CLAUDE_CODE_SUBAGENT_MODEL",
            "--subagent",
            "subagent",
            "~anthropic/claude-opus-latest"
        ),
    ];

    /// <summary>
    /// Shown when the catalog cannot be fetched. Deliberately short: it only has to cover the
    /// choices the wizard itself recommends, and anything else can be typed in as a slug.
    /// </summary>
    public static readonly Model[] Fallback =
    [
        new(Auto, "Auto Router", 0, null, null),
        new(AutoBeta, "Auto Router (beta)", 0, null, null),
        new("~anthropic/claude-opus-latest", "Claude Opus (latest)", 0, null, null),
        new("~anthropic/claude-sonnet-latest", "Claude Sonnet (latest)", 0, null, null),
        new("~anthropic/claude-haiku-latest", "Claude Haiku (latest)", 0, null, null),
        new("~anthropic/claude-fable-latest", "Claude Fable (latest)", 0, null, null),
        new("anthropic/claude-opus-5", "Claude Opus 5", 1_000_000, 10, 50),
        new("anthropic/claude-sonnet-5", "Claude Sonnet 5", 1_000_000, 3, 15),
        new("anthropic/claude-fable-5", "Claude Fable 5", 200_000, 1, 5),
        new("anthropic/claude-haiku-4.5", "Claude Haiku 4.5", 200_000, 1, 5),
    ];

    /// <summary>True if this profile's settings point Claude Code at OpenRouter. Read from the same
    /// file that configures the behaviour, so it cannot disagree with reality.</summary>
    public static bool Configures(IReadOnlyDictionary<string, string> env) =>
        env.TryGetValue(BaseUrlVariable, out var url)
        && url.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>The env entries that make a profile an OpenRouter profile.</summary>
    public static List<KeyValuePair<string, string?>> Configure(
        string? key,
        IReadOnlyDictionary<string, string>? models = null
    )
    {
        var values = new List<KeyValuePair<string, string?>>
        {
            new(BaseUrlVariable, BaseUrl),
            // Explicitly empty rather than absent: a non-empty ANTHROPIC_API_KEY inherited from the
            // environment would be tried first and fail against OpenRouter.
            new(ApiKeyVariable, ""),
        };

        if (key is not null)
            values.Add(new(AuthTokenVariable, key));

        if (models is not null)
        {
            foreach (var slot in Slots)
            {
                if (models.TryGetValue(slot.Variable, out var model))
                    values.Add(new(slot.Variable, model));
            }
        }

        return values;
    }

    public static Catalog FetchCatalog(TimeSpan timeout)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.Add("User-Agent", "tabard");

            using var response = client.Send(
                new HttpRequestMessage(HttpMethod.Get, ModelsUrl),
                HttpCompletionOption.ResponseHeadersRead
            );

            if (!response.IsSuccessStatusCode)
                return new Catalog(
                    Fallback,
                    false,
                    $"OpenRouter answered {(int)response.StatusCode}"
                );

            using var stream = response.Content.ReadAsStream();
            var models = ParseCatalog(stream);

            return models.Count == 0
                ? new Catalog(Fallback, false, "OpenRouter returned no usable models")
                : new Catalog(models, true);
        }
        catch (Exception ex)
        {
            return new Catalog(Fallback, false, ex.Message);
        }
    }

    /// <summary>
    /// Reads /v1/models. Filtering happens here rather than through the API's
    /// ?supported_parameters=tools, which drops the '~vendor/model-latest' aliases and the
    /// openrouter/auto routers - the very models this wizard defaults to - because their parameters
    /// are only known once a request is routed.
    /// </summary>
    public static List<Model> ParseCatalog(Stream stream)
    {
        var models = new List<Model>();

        using var doc = JsonDocument.Parse(stream);

        if (
            doc.RootElement.ValueKind is not JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind is not JsonValueKind.Array
        )
        {
            return models;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (
                entry.ValueKind is not JsonValueKind.Object
                || !entry.TryGetProperty("id", out var id)
                || id.ValueKind is not JsonValueKind.String
                || id.GetString() is not { Length: > 0 } slug
                || !UsableForClaudeCode(slug, entry)
            )
            {
                continue;
            }

            models.Add(
                new Model(
                    slug,
                    Text(entry, "name") ?? slug,
                    Number(entry, "context_length"),
                    Price(entry, "prompt"),
                    Price(entry, "completion")
                )
            );
        }

        models.Sort(
            (a, b) =>
            {
                var byRank = Rank(a.Id).CompareTo(Rank(b.Id));
                return byRank != 0 ? byRank : string.CompareOrdinal(a.Id, b.Id);
            }
        );

        return models;
    }

    /// <summary>Claude Code cannot work without tool calls, so a model that does not support them is
    /// noise in the list. Routers and aliases declare nothing and are kept regardless.</summary>
    private static bool UsableForClaudeCode(string slug, JsonElement entry)
    {
        if (IsAlias(slug) || IsRouter(slug))
            return true;

        if (
            !entry.TryGetProperty("supported_parameters", out var parameters)
            || parameters.ValueKind is not JsonValueKind.Array
        )
        {
            return false;
        }

        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.ValueKind is JsonValueKind.String && parameter.ValueEquals("tools"))
                return true;
        }

        return false;
    }

    private static bool IsAlias(string slug) => slug.StartsWith('~');

    private static bool IsRouter(string slug) =>
        slug.StartsWith("openrouter/auto", StringComparison.Ordinal);

    private static int Rank(string slug) =>
        IsRouter(slug) ? 0
        : IsAlias(slug) ? 1
        : 2;

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>Prices arrive as per-token decimal strings; per million is the unit people quote.</summary>
    private static double? Price(JsonElement entry, string name)
    {
        if (
            !entry.TryGetProperty("pricing", out var pricing)
            || pricing.ValueKind is not JsonValueKind.Object
            || Text(pricing, name) is not { } raw
            || !double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
        {
            return null;
        }

        return value * 1_000_000;
    }

    /// <summary>
    /// Asks OpenRouter whether the key works. A network failure is reported as Unknown rather than
    /// Rejected - refusing to save a good key because the wifi is down would be worse than saving a
    /// bad one, which Claude Code will complain about soon enough.
    /// </summary>
    public static KeyCheck ValidateKey(string key, TimeSpan timeout)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.Add("User-Agent", "tabard");

            using var request = new HttpRequestMessage(HttpMethod.Get, KeyUrl);
            request.Headers.Add("Authorization", $"Bearer {key}");

            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new KeyCheck(KeyStatus.Rejected);

            if (!response.IsSuccessStatusCode)
                return new KeyCheck(
                    KeyStatus.Unknown,
                    Error: $"OpenRouter answered {(int)response.StatusCode}"
                );

            using var stream = response.Content.ReadAsStream();
            return ParseKey(stream);
        }
        catch (Exception ex)
        {
            return new KeyCheck(KeyStatus.Unknown, Error: ex.Message);
        }
    }

    public static KeyCheck ParseKey(Stream stream)
    {
        try
        {
            using var doc = JsonDocument.Parse(stream);

            if (
                doc.RootElement.ValueKind is not JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind is not JsonValueKind.Object
            )
            {
                return new KeyCheck(KeyStatus.Valid);
            }

            double? remaining =
                data.TryGetProperty("limit_remaining", out var limit)
                && limit.ValueKind is JsonValueKind.Number
                && limit.TryGetDouble(out var value)
                    ? value
                    : null;

            var free =
                data.TryGetProperty("is_free_tier", out var tier)
                && tier.ValueKind is JsonValueKind.True;

            return new KeyCheck(KeyStatus.Valid, Text(data, "label"), remaining, free);
        }
        catch
        {
            // A 200 is the answer that matters; the body is only ever decoration.
            return new KeyCheck(KeyStatus.Valid);
        }
    }

    /// <summary>Enough of the key to recognise it, not enough to use it.</summary>
    public static string Redact(string key)
    {
        if (key.Length <= 8)
            return new string('*', key.Length);

        return $"{key[..Math.Min(9, key.Length - 4)]}...{key[^4..]}";
    }
}
