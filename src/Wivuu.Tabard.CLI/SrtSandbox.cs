using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Anthropic's sandbox runtime (srt) as an <see cref="ISandbox"/>: the agent installed here, run
/// under the operating system's own sandbox - Seatbelt on macOS, bubblewrap on Linux - with a
/// policy tabard writes for each launch. Nothing to build and nothing left running, so a launch is
/// as quick as a direct one.
///
/// The policy keeps what a Docker sandbox would. The agent can change the folder (or repository) it
/// was launched in and its own profile, minus the parts of the profile that run code or instruct a
/// later session (<see cref="Sandboxing.ReadOnlyPaths"/>). It cannot read other profiles or the
/// usual credential stores. It reaches the network only through srt's proxy, and only the sites
/// its agent needs, the ones the profile adds (<see cref="SandboxOptions"/>), and the local port
/// it waits for.
/// </summary>
internal sealed class SrtSandbox : ISandbox
{
    public const string Program = "srt";

    public const string Package = "@anthropic-ai/sandbox-runtime";

    public string Name => "srt";

    public string Description => "an srt sandbox";

    public string Executable => Program;

    public string Install => $"npm install -g {Package}";

    /// <summary>Seatbelt on macOS, bubblewrap on Linux. Windows support is srt's alpha, and runs
    /// the agent as an account set up with an admin prompt.</summary>
    public bool SupportedHere => !OperatingSystem.IsWindows();

    public string? Unsupported(Profile profile)
    {
        // srt's Windows support runs the agent as a separate user it has to be installed for, with
        // an admin prompt, and is still marked alpha.
        if (OperatingSystem.IsWindows())
            return $"'{profile.Name}' is set to launch in an srt sandbox, which tabard does not "
                + $"support on Windows yet. Run 'tabard sandbox off {profile.Name}' to launch it "
                + "directly.";

        return null;
    }

    public SandboxLaunch Prepare(
        Profile profile,
        IReadOnlyList<string> agentArgs,
        SandboxRequest request
    )
    {
        if (Unsupported(profile) is { } reason)
            throw new PlatformNotSupportedException(reason);

        var srt =
            Launcher.Which(Program)
            ?? throw new FileNotFoundException(
                $"'{profile.Name}' is set to launch in an srt sandbox, but 'srt' is not on PATH. "
                    + $"Install it with 'npm install -g {Package}', pick another kind with "
                    + $"'tabard sandbox kind {profile.Name} sbx', or run "
                    + $"'tabard sandbox off {profile.Name}' to launch it directly."
            );

        var command = Harnesses.Executable(profile.Harness);
        var agent =
            Launcher.Which(command)
            ?? throw new FileNotFoundException(
                $"could not find '{command}' on PATH. Install {Harnesses.DisplayName(profile.Harness)} "
                    + "first, or put it on PATH."
            );

        var warnings = new List<string>();
        var workspace = Sandboxing.ResolveWorkspace(request.Cwd, warnings);
        Sandboxing.Prepare(profile.Dir, profile.Harness);

        foreach (var warning in warnings)
            Console.Error.WriteLine($"tabard: {warning}");

        var configDir = Path.GetFullPath(profile.Dir);
        var temp = TempDir(profile.Name);
        Directory.CreateDirectory(temp);

        var loopback = request
            .HostPorts.Concat(
                profile.Harness is Harness.Pi ? PiEndpoints(configDir).LoopbackPorts : []
            )
            .Distinct()
            .Order()
            .ToList();

        var sites = AgentSites(profile.Harness, configDir)
            .Concat(DeveloperSites)
            .Concat(SandboxOptions.AllowedOf(profile.Name))
            .Concat(loopback.SelectMany(port => new[] { $"127.0.0.1:{port}", $"localhost:{port}" }))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var policy = Policy(
            configDir,
            profile.Harness,
            workspace,
            temp,
            sites,
            Paths.Home,
            Resolve(agent)
        );
        var settings = WriteSettings(policy);

        var psi = new ProcessStartInfo(srt) { UseShellExecute = false };
        foreach (var arg in Args(settings, agent, agentArgs, loopback.Count > 0))
            psi.ArgumentList.Add(arg);

        psi.Environment[Harnesses.ConfigVariable(profile.Harness)] = configDir;
        foreach (var variable in new[] { "TMPDIR", "TMP", "TEMP" })
            psi.Environment[variable] = temp;

        Console.Error.WriteLine(
            $"tabard: launching '{profile.Name}' in an srt sandbox - it can change "
                + $"{Paths.Pretty(workspace.Root)} and its profile, and reach "
                + $"{(sites.Count == 1 ? "1 site" : $"{sites.Count} sites")}."
        );

        return new SandboxLaunch(psi, Program, () => TryDelete(settings));
    }

    /// <summary>
    /// The arguments for 'srt'. When the agent has to reach a port on this machine, it starts
    /// through a short script that takes loopback out of NO_PROXY: srt leaves loopback to direct
    /// connections, which its sandbox refuses on macOS and which reach the sandbox's own loopback
    /// on Linux. Through srt's proxy, 127.0.0.1:PORT is the host's, and only the allowed ports.
    /// </summary>
    internal static List<string> Args(
        string settings,
        string agent,
        IReadOnlyList<string> agentArgs,
        bool viaProxyToLoopback
    )
    {
        List<string> args = ["-s", settings, "--"];

        if (viaProxyToLoopback)
            args.AddRange(["/bin/sh", "-c", LoopbackScript, "tabard-srt"]);

        args.Add(agent);
        args.AddRange(agentArgs);
        return args;
    }

    /// <summary>sh -c SCRIPT NAME AGENT ARGS...: "$@" is the agent and its arguments.</summary>
    internal const string LoopbackScript = """
        set -f
        strip() {
          out=""
          old=$IFS
          IFS=,
          for host in $1; do
            case $host in
              localhost|127.0.0.1|::1|"[::1]") ;;
              *) out="${out:+$out,}$host" ;;
            esac
          done
          IFS=$old
          printf '%s' "$out"
        }
        NO_PROXY=$(strip "$NO_PROXY")
        no_proxy=$(strip "$no_proxy")
        export NO_PROXY no_proxy
        exec "$@"
        """;

    /// <summary>What srt is told, before it is written as its settings file.</summary>
    internal sealed record SrtPolicy(
        IReadOnlyList<string> DenyRead,
        IReadOnlyList<string> AllowRead,
        IReadOnlyList<string> AllowWrite,
        IReadOnlyList<string> DenyWrite,
        IReadOnlyList<string> AllowedDomains,
        bool AllowPty
    );

    /// <summary>
    /// Credential stores and other agents' logins, under the home directory. Reading is otherwise
    /// open - the agent needs its own installation, the toolchains, and the rest of the system -
    /// so these are what keep a sandboxed session from walking off with someone else's keys.
    /// </summary>
    private static readonly string[] SecretsUnderHome =
    [
        ".ssh",
        ".gnupg",
        ".aws",
        ".azure",
        ".config/gcloud",
        ".config/gh",
        ".kube",
        ".docker",
        ".netrc",
        ".npmrc",
        ".pypirc",
        ".git-credentials",
        ".password-store",
        // Not ~/Library/Keychains: Claude Code keeps its login there, and reads it through the
        // Keychain's own per-item access control, which still applies inside the sandbox.
        // The logins tabard does not manage: Claude Code's and pi's own defaults.
        ".claude",
        ".claude.json",
        ".pi/agent/auth.json",
        ".pi/agent/sessions",
    ];

    internal static SrtPolicy Policy(
        string configDir,
        Harness harness,
        Workspace workspace,
        string temp,
        IReadOnlyList<string> sites,
        string home,
        string? agent = null
    )
    {
        // Every other profile, and the lists that decide whether this one is sandboxed at all.
        // Its own directory is read back below - srt lets allowRead win over denyRead.
        var denyRead = new List<string> { Path.GetFullPath(Paths.TabardRoot) };
        denyRead.AddRange(
            SecretsUnderHome
                .Select(rel => Path.Combine(home, rel))
                // An older Claude Code lives in ~/.claude/local: one that holds the agent itself
                // has to stay readable, or the agent cannot start.
                .Where(path => agent is null || !Within(path, agent))
                // tabard's own links to the launched profile, which Claude Code reads even with
                // CLAUDE_CONFIG_DIR set. Where they lead is already decided by the rules on
                // ~/.tabard; denying the link as well would hide the profile from itself.
                .Where(path => !LinksIntoTabard(path))
        );

        List<string> allowWrite = [workspace.Root, .. workspace.Writable, configDir, temp];

        // denyWrite beats allowWrite in srt, so these hold even inside the folders above.
        List<string> denyWrite =
        [
            .. Sandboxing.ReadOnlyPaths(configDir, harness),
            .. workspace.ReadOnly,
        ];

        return new SrtPolicy(
            denyRead,
            [configDir],
            allowWrite.Distinct(StringComparer.Ordinal).ToList(),
            denyWrite.Distinct(StringComparer.Ordinal).ToList(),
            sites,
            // Without it an interactive agent cannot put the terminal into raw mode, and exits.
            // Only macOS's sandbox asks.
            OperatingSystem.IsMacOS()
        );
    }

    private static bool LinksIntoTabard(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is null)
                return false;

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return target is not null && Within(Path.GetFullPath(Paths.TabardRoot), target);
        }
        catch
        {
            return false;
        }
    }

    private static bool Within(string folder, string path) =>
        string.Equals(folder, path, StringComparison.Ordinal)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Where a launcher on PATH really lives: ~/.local/bin/claude is a link into the
    /// installation.</summary>
    private static string Resolve(string executable)
    {
        try
        {
            return File.ResolveLinkTarget(executable, returnFinalTarget: true)?.FullName
                ?? executable;
        }
        catch
        {
            return executable;
        }
    }

    internal static string Json(SrtPolicy policy)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("filesystem");
            Array(writer, "denyRead", policy.DenyRead);
            Array(writer, "allowRead", policy.AllowRead);
            Array(writer, "allowWrite", policy.AllowWrite);
            Array(writer, "denyWrite", policy.DenyWrite);
            writer.WriteEndObject();

            writer.WriteStartObject("network");
            Array(writer, "allowedDomains", policy.AllowedDomains);
            Array(writer, "deniedDomains", []);
            writer.WriteEndObject();

            if (policy.AllowPty)
                writer.WriteBoolean("allowPty", true);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void Array(Utf8JsonWriter writer, string name, IEnumerable<string> values)
        {
            writer.WriteStartArray(name);
            foreach (var value in values)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// The sites the agent cannot work without. Claude Code talks to Anthropic. pi talks to
    /// whichever providers the profile is set up for: the endpoints in its models.json, and the
    /// built-in providers it has a login for or defaults to.
    /// </summary>
    internal static List<string> AgentSites(Harness harness, string configDir)
    {
        if (harness is not Harness.Pi)
            return [.. AnthropicSites];

        var sites = new List<string>(PiEndpoints(configDir).Hosts);

        foreach (var provider in PiProviders(configDir))
        {
            if (PiProviderSites.TryGetValue(provider, out var known))
                sites.AddRange(known);
        }

        return sites.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Open to every srt sandbox, so the everyday work of an agent - cloning, pushing over HTTPS,
    /// installing packages - goes through without each profile listing them. Code hosts and the
    /// main package registries, and nothing that serves arbitrary user content beyond what they do.
    /// </summary>
    internal static readonly string[] DeveloperSites =
    [
        // Code hosts.
        "github.com",
        "*.github.com",
        "*.githubusercontent.com",
        "ghcr.io",
        "gitlab.com",
        "*.gitlab.com",
        "bitbucket.org",
        "*.bitbucket.org",
        // JavaScript.
        "registry.npmjs.org",
        "*.npmjs.org",
        "*.npmjs.com",
        "registry.yarnpkg.com",
        "*.yarnpkg.com",
        "jsr.io",
        "*.jsr.io",
        "bun.sh",
        "deno.land",
        "*.deno.land",
        "nodejs.org",
        // Python.
        "pypi.org",
        "*.pypi.org",
        "files.pythonhosted.org",
        "astral.sh",
        // .NET.
        "api.nuget.org",
        "*.nuget.org",
        "dotnetcli.azureedge.net",
        "builds.dotnet.microsoft.com",
        // Rust.
        "crates.io",
        "*.crates.io",
        "static.rust-lang.org",
        "sh.rustup.rs",
        // Go.
        "proxy.golang.org",
        "sum.golang.org",
        "go.dev",
        "*.go.dev",
        // Ruby, Java and the JVM, PHP.
        "rubygems.org",
        "*.rubygems.org",
        "repo.maven.apache.org",
        "repo1.maven.org",
        "*.maven.org",
        "plugins.gradle.org",
        "services.gradle.org",
        "packagist.org",
        "*.packagist.org",
    ];

    private static readonly string[] AnthropicSites =
    [
        "anthropic.com",
        "*.anthropic.com",
        "claude.ai",
        "*.claude.ai",
        "claude.com",
        "*.claude.com",
    ];

    /// <summary>pi's built-in providers, by the name its settings and auth.json use.</summary>
    private static readonly Dictionary<string, string[]> PiProviderSites = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["anthropic"] = AnthropicSites,
        ["openai"] = ["api.openai.com", "auth.openai.com", "chatgpt.com"],
        ["openai-codex"] = ["api.openai.com", "auth.openai.com", "chatgpt.com"],
        ["google"] = ["generativelanguage.googleapis.com", "oauth2.googleapis.com"],
        ["google-gemini-cli"] = ["cloudcode-pa.googleapis.com", "oauth2.googleapis.com"],
        ["openrouter"] = ["openrouter.ai"],
        ["groq"] = ["api.groq.com"],
        ["xai"] = ["api.x.ai"],
        ["mistral"] = ["api.mistral.ai"],
        ["cerebras"] = ["api.cerebras.ai"],
        ["github-copilot"] = ["github.com", "api.github.com", "*.githubcopilot.com"],
    };

    /// <summary>The providers a pi profile defaults to or has a login for.</summary>
    internal static List<string> PiProviders(string configDir)
    {
        var providers = new List<string>();

        if (ReadJson(Path.Combine(configDir, "settings.json")) is { } settings)
        {
            using (settings)
            {
                if (
                    settings.RootElement.ValueKind is JsonValueKind.Object
                    && settings.RootElement.TryGetProperty("defaultProvider", out var p)
                    && p.ValueKind is JsonValueKind.String
                )
                {
                    providers.Add(p.GetString()!);
                }
            }
        }

        // Only the names: auth.json holds the keys themselves, and none of them is read.
        if (ReadJson(Path.Combine(configDir, "auth.json")) is { } auth)
        {
            using (auth)
            {
                if (auth.RootElement.ValueKind is JsonValueKind.Object)
                    providers.AddRange(auth.RootElement.EnumerateObject().Select(e => e.Name));
            }
        }

        return providers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal sealed record Endpoints(IReadOnlyList<string> Hosts, IReadOnlyList<int> LoopbackPorts);

    /// <summary>
    /// Where the providers in a pi profile's models.json send their requests: each base URL's host,
    /// with its port if it names one. A base URL on this machine is a port to reach through the
    /// proxy instead (<see cref="Args"/>).
    /// </summary>
    internal static Endpoints PiEndpoints(string configDir)
    {
        var hosts = new List<string>();
        var loopback = new List<int>();

        using var doc = ReadJson(Path.Combine(configDir, "models.json"));
        if (
            doc is null
            || doc.RootElement.ValueKind is not JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("providers", out var providers)
            || providers.ValueKind is not JsonValueKind.Object
        )
        {
            return new Endpoints(hosts, loopback);
        }

        foreach (var provider in providers.EnumerateObject())
        {
            if (
                provider.Value.ValueKind is not JsonValueKind.Object
                || !provider.Value.TryGetProperty("baseUrl", out var url)
                || url.ValueKind is not JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
            )
            {
                continue;
            }

            if (uri.IsLoopback)
            {
                loopback.Add(uri.Port);
                continue;
            }

            hosts.Add(uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}");
        }

        return new Endpoints(hosts.Distinct().ToList(), loopback.Distinct().ToList());
    }

    private static JsonDocument? ReadJson(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;

            using var stream = File.OpenRead(file);
            return JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A temp folder of the profile's own. srt opens no shared temp folder for writing,
    /// and the whole of one would let a session leave files for other programs here to pick up.
    /// </summary>
    internal static string TempDir(string profile) =>
        Path.Combine(Path.GetTempPath(), "tabard-srt", profile);

    /// <summary>
    /// The policy, as a settings file under ~/.tabard - which the sandbox cannot read - named for
    /// this launch, so two at once cannot overwrite each other's. Deleted once the agent exits;
    /// ones a killed launch left behind are swept up here.
    /// </summary>
    private static string WriteSettings(SrtPolicy policy)
    {
        var dir = Path.Combine(Paths.TabardRoot, "run");
        Directory.CreateDirectory(dir);

        try
        {
            foreach (var stale in Directory.EnumerateFiles(dir, "srt-*.json"))
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1))
                    TryDelete(stale);
            }
        }
        catch
        {
            // Tidying only.
        }

        var file = Path.Combine(dir, $"srt-{Environment.ProcessId}.json");
        File.WriteAllText(file, Json(policy));
        return file;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // Inert once the launch that wrote it has gone.
        }
    }
}
