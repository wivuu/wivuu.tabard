using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// pi inside Docker Sandboxes. sbx has no pi agent, so tabard runs its 'shell' agent with a short
/// script that starts pi, in sandboxes made from a template holding Node.js and the same pi version
/// as this machine. The template is built once per pi version - the first sandboxed launch after
/// installing or updating pi pays for it - and every sandbox after that starts from it.
/// </summary>
internal static partial class Sbx
{
    /// <summary>The sbx agent pi runs under: a plain shell that runs what it is given.</summary>
    internal const string ShellAgent = "shell";

    /// <summary>Templates are tagged <c>tabard-pi:&lt;version&gt;</c>.</summary>
    internal const string PiTemplateRepository = "tabard-pi";

    /// <summary>The npm package pi is published as. The installer on this machine pins the same
    /// one, so the template runs exactly what a direct launch would.</summary>
    internal const string PiPackage = "@earendil-works/pi-coding-agent";

    /// <summary>pi needs a newer Node.js than Debian ships.</summary>
    private const string NodeMajor = "22";

    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The tag of a template holding pi at the version installed here, building it first if there
    /// is not one. Throws with the build log's path if the build fails.
    /// </summary>
    public static string EnsurePiTemplate(string executable)
    {
        var version = HostPiVersion() ?? "latest";
        var tag = $"{PiTemplateRepository}:{version}";

        if (TemplateExists(executable, tag))
            return tag;

        BuildPiTemplate(executable, version, tag);
        return tag;
    }

    /// <summary>The version of pi on PATH here, or null if it cannot be asked.</summary>
    internal static string? HostPiVersion()
    {
        if (Launcher.Which("pi") is not { } pi)
            return null;

        var (code, output) = Capture(pi, ["--version"], TimeSpan.FromSeconds(30));
        return code == 0 ? ParseVersion(output) : null;
    }

    /// <summary>The last line of <paramref name="output"/> that is a plain version - pi can print
    /// Node's warnings first - or null.</summary>
    internal static string? ParseVersion(string output) =>
        output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => VersionPattern().IsMatch(line));

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+-]{0,63}$")]
    private static partial Regex VersionPattern();

    /// <summary>Whether 'sbx template ls' lists <paramref name="tag"/>. sbx qualifies a bare name
    /// with docker.io/library/, so either spelling counts.</summary>
    internal static bool TemplateListed(string json, string tag)
    {
        var colon = tag.LastIndexOf(':');
        var (repository, version) = (tag[..colon], tag[(colon + 1)..]);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is not JsonValueKind.Array)
                return false;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (
                    entry.ValueKind is JsonValueKind.Object
                    && entry.TryGetProperty("repository", out var repo)
                    && entry.TryGetProperty("tag", out var t)
                    && repo.ValueKind is JsonValueKind.String
                    && t.ValueKind is JsonValueKind.String
                    && t.GetString() == version
                    && repo.GetString() is { } name
                    && (
                        name == repository
                        || name.EndsWith("/" + repository, StringComparison.Ordinal)
                    )
                )
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable: build it, which is slow but right.
        }

        return false;
    }

    private static bool TemplateExists(string executable, string tag)
    {
        var (code, output) = Capture(
            executable,
            ["template", "ls", "--format", "json"],
            TimeSpan.FromSeconds(30)
        );
        return code == 0 && TemplateListed(output, tag);
    }

    /// <summary>The root script that turns sbx's shell image into one that runs pi.</summary>
    internal static string PiInstallScript(string version) =>
        $"""
            set -e
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq
            apt-get install -y -qq nodejs npm
            npm install -g --silent n
            n -q {NodeMajor}
            hash -r
            /usr/local/bin/npm install -g --silent {PiPackage}@{version}
            """;

    private static void BuildPiTemplate(string executable, string version, string tag)
    {
        var log = Path.Combine(Paths.TabardRoot, "logs", "sbx-pi-template.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        var builder = $"tabard-pi-template-{Environment.ProcessId}";
        var workspace = Path.Combine(Path.GetTempPath(), builder);
        Directory.CreateDirectory(workspace);

        Console.Error.WriteLine(
            $"tabard: building a sandbox template with pi {version} - once per pi version, and it "
                + $"takes a few minutes. Progress goes to {Paths.Pretty(log)}."
        );

        using var output = new StreamWriter(log, append: false) { AutoFlush = true };

        void Step(string what, params string[] args)
        {
            Console.Error.WriteLine($"tabard:   {what}");
            output.WriteLine($"--- {what}: sbx {string.Join(' ', args)}");

            // Never a prompt nobody can see: a question sbx would ask fails at once, with the
            // command that would answer it in the log.
            var (code, text) = Capture(executable, ["--no-input", .. args], StepTimeout);
            output.Write(text);

            if (code != 0)
                throw new InvalidOperationException(
                    $"could not build the pi sandbox template ({what} failed). See {Paths.Pretty(log)}."
                );
        }

        try
        {
            Step(
                "starting a builder sandbox",
                "run",
                "-d",
                "--name",
                builder,
                ShellAgent,
                workspace
            );
            Step(
                $"installing Node.js {NodeMajor} and pi {version}",
                "exec",
                "-u",
                "root",
                builder,
                "sh",
                "-c",
                PiInstallScript(version)
            );
            Step("checking pi runs", "exec", builder, "pi", "--version");
            // sbx only snapshots a stopped sandbox, and otherwise stops to ask.
            Step("stopping the builder", "stop", builder);
            Step($"saving it as {tag}", "template", "save", builder, tag);
        }
        finally
        {
            Capture(executable, ["rm", "-f", builder], TimeSpan.FromMinutes(2));

            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // An empty folder in the temp directory is not worth failing over.
            }
        }
    }

    /// <summary>
    /// Forwards 127.0.0.1:PORT inside the sandbox to the same port on the host, so a pi profile
    /// pointed at a server on this machine works unchanged in a sandbox, where 127.0.0.1 is the
    /// sandbox itself. Python because sbx's shell image has it. No single quotes, so the script
    /// below can quote it whole.
    /// </summary>
    internal const string Forwarder = """
        import asyncio, sys
        port = int(sys.argv[1])
        async def pipe(r, w):
            try:
                while data := await r.read(65536):
                    w.write(data)
                    await w.drain()
            except Exception:
                pass
            finally:
                w.close()
        async def handle(r, w):
            try:
                ur, uw = await asyncio.open_connection("host.docker.internal", port)
            except Exception:
                w.close()
                return
            await asyncio.gather(pipe(r, uw), pipe(ur, w))
        async def main():
            server = await asyncio.start_server(handle, "127.0.0.1", port)
            async with server:
                await server.serve_forever()
        asyncio.run(main())
        """;

    /// <summary>
    /// What the shell agent runs: a forwarder per port in TABARD_FORWARD, then pi with the
    /// launch's arguments, then the forwarders' end. A forwarder that cannot bind is one left by an
    /// earlier session and still serving, so it is ignored.
    /// </summary>
    internal static string PiScript =>
        $$"""
            forwarders=""
            for port in $TABARD_FORWARD; do
              python3 -c '{{Forwarder}}' "$port" >/dev/null 2>&1 &
              forwarders="$forwarders $!"
            done
            pi "$@"
            status=$?
            [ -n "$forwarders" ] && kill $forwarders 2>/dev/null
            exit $status
            """;

    /// <summary>
    /// The arguments for 'sbx' to run pi. Like <see cref="RunArgs"/>: creating takes the template
    /// and the mounts, going back in takes only the name, and the environment goes both times.
    /// </summary>
    public static List<string> PiRunArgs(
        string sandbox,
        string profileDir,
        Workspace workspace,
        bool exists,
        IReadOnlyList<string> piArgs,
        string template,
        IReadOnlyList<int> hostPorts,
        IReadOnlyList<string>? environment = null
    )
    {
        var configDir = Path.GetFullPath(profileDir);

        List<string> args = exists
            ? ["run", "--name", sandbox]
            : ["run", ShellAgent, "--name", sandbox, "--pull", "never", "-t", template];

        if (!string.Equals(workspace.WorkDir, workspace.Root, StringComparison.Ordinal))
            args.AddRange(["-w", workspace.WorkDir]);

        args.AddRange(["-e", $"PI_CODING_AGENT_DIR={configDir}"]);
        args.AddRange(["-e", $"TABARD_FORWARD={string.Join(' ', hostPorts)}"]);

        foreach (var variable in environment ?? [])
            args.AddRange(["-e", variable]);

        if (!exists)
        {
            args.Add(workspace.Root);
            args.AddRange(workspace.Writable);
            args.Add(configDir);
            args.AddRange(workspace.ReadOnly.Select(path => $"{path}:ro"));
            args.AddRange(
                Sandboxing.ReadOnlyPaths(configDir, Harness.Pi).Select(path => $"{path}:ro")
            );
        }

        // bash -c SCRIPT NAME ARGS...: NAME becomes $0, and the launch's arguments "$@".
        args.AddRange(["--", "-c", PiScript, "tabard-pi"]);
        args.AddRange(piArgs);

        return args;
    }

    /// <summary>Runs a command to completion with its output - both streams - captured.</summary>
    private static (int Code, string Output) Capture(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout
    )
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, "");

            proc.StandardInput.Close();
            var stderr = proc.StandardError.ReadToEndAsync();
            var stdout = proc.StandardOutput.ReadToEndAsync();

            if (!proc.WaitForExit(timeout))
            {
                proc.Kill(entireProcessTree: true);
                return (-1, $"timed out after {timeout.TotalMinutes:0} minutes\n");
            }

            return (proc.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message + "\n");
        }
    }
}
