using System.Diagnostics;
using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// A command a profile starts alongside its agent and stops when the agent exits - a local model
/// server for a profile that talks to one, say.
///
/// Kept in ~/.tabard/companions.json, outside every profile, for the same reason as the sandbox
/// list: it runs on the host, and a sandboxed session can write to its own profile. JSON because a
/// command holds spaces and could hold anything else.
/// </summary>
internal static class Companions
{
    /// <summary>How long a companion gets to shut down after being asked before it is killed.
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    public static string LogFile(string profile) =>
        Path.Combine(Paths.TabardRoot, "logs", $"{profile}.log");

    /// <summary>The command to run alongside the profile's agent, or null.</summary>
    public static string? Of(string profile) => Read().GetValueOrDefault(profile)?.Command;

    /// <summary>The address the agent's launch waits on, or null to launch straight away.</summary>
    public static string? WaitForOf(string profile) => Read().GetValueOrDefault(profile)?.WaitFor;

    /// <summary>Null or blank clears it. Callers hold <see cref="ProfileStore.AcquireLock"/>.
    /// </summary>
    public static void Set(string profile, string? command)
    {
        command = string.IsNullOrWhiteSpace(command) ? null : command.Trim();

        if (command is not null && command.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("a command to run alongside must fit on one line.");

        Update(profile, entry => entry with { Command = command });
    }

    /// <summary>
    /// A port, or host:port, that must accept a connection before the agent launches. Null or blank
    /// clears it. Stored as typed, once <see cref="ParseAddress"/> accepts it. Callers hold
    /// <see cref="ProfileStore.AcquireLock"/>.
    /// </summary>
    public static void SetWaitFor(string profile, string? address)
    {
        address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();

        if (address is not null)
            _ = ParseAddress(address);

        Update(profile, entry => entry with { WaitFor = address });
    }

    private static void Update(string profile, Func<Entry, Entry> change)
    {
        var entries = Read();
        var was = entries.GetValueOrDefault(profile) ?? new Entry(null, null);
        var now = change(was);

        if (now == was)
            return;

        if (now is { Command: null, WaitFor: null })
            entries.Remove(profile);
        else
            entries[profile] = now;

        Write(entries);
    }

    /// <summary>
    /// '8080' means localhost; otherwise 'host:port', with an IPv6 host in brackets. Throws an
    /// <see cref="ArgumentException"/> worded for the editor's status line.
    /// </summary>
    internal static (string Host, int Port) ParseAddress(string address)
    {
        string host = "localhost";
        var portText = address;

        var colon = address.LastIndexOf(':');
        if (colon >= 0)
        {
            host = address[..colon].Trim('[', ']');
            portText = address[(colon + 1)..];
        }

        if (
            host.Length == 0
            || host.Any(char.IsWhiteSpace)
            || host.Contains('/')
            // Only a bracketed IPv6 host may hold a colon - anything else is a URL or a typo.
            || (host.Contains(':') && !address.StartsWith('['))
            || !int.TryParse(
                portText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var port
            )
            || port is < 1 or > 65535
        )
        {
            throw new ArgumentException(
                $"'{address}' is not a port or host:port - try 8080 or localhost:8080."
            );
        }

        return (host, port);
    }

    /// <summary>
    /// Holds the launch until the profile's address accepts a connection. Returns null to go on and
    /// launch, or the exit code to leave with instead: 130 if the user pressed ^C, 1 if the command
    /// alongside exited first. That one does not launch anyway, because nothing is coming up to
    /// answer - and an agent drawn over the explanation is how a bad command goes unnoticed.
    /// </summary>
    public static int? WaitFor(Profile profile, Running? running)
    {
        if (WaitForOf(profile.Name) is not { } address)
            return null;

        (string Host, int Port) target;
        try
        {
            target = ParseAddress(address);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"tabard: not waiting: {ex.Message}");
            return null;
        }

        using var cancel = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancel.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        WaitDisplay? display = null;
        try
        {
            if (Answers(target, cancel.Token))
                return null;

            // Redrawn in place on a terminal; plain lines anywhere else - a log, a pipe, CI.
            display = WaitDisplay.Create(address, running);
            if (display is null)
                Console.Error.WriteLine(
                    $"tabard: waiting for {address} to answer before launching - ^C to give up."
                );

            var started = Stopwatch.StartNew();
            var nextNote = TimeSpan.FromSeconds(30);
            var nextProbe = TimeSpan.Zero;
            Task<bool>? probe = null;

            while (!cancel.IsCancellationRequested)
            {
                if (running is { HasExited: true })
                {
                    var why =
                        $"'{running.Command}' exited before {address} answered, so "
                        + $"'{profile.Name}' was not launched.";

                    if (display is null)
                        Console.Error.WriteLine(
                            $"tabard: {why} The end of {Paths.Pretty(running.Log)}:"
                        );
                    else
                        display.Fail($"{why} The end of {Paths.Pretty(running.Log)}:");

                    foreach (var line in running.Tail(15))
                        Console.Error.WriteLine($"  {line}");

                    return 1;
                }

                // A connection attempt can take up to a second, so it runs beside the drawing,
                // which would otherwise freeze for each one.
                if (probe is null && started.Elapsed >= nextProbe)
                    probe = Task.Run(() => Answers(target, cancel.Token));

                if (probe is { IsCompleted: true })
                {
                    if (probe.Result)
                    {
                        if (display is null)
                            Console.Error.WriteLine(
                                $"tabard: {address} answered after {started.Elapsed.TotalSeconds:0}s."
                            );
                        else
                            display.Succeed(started.Elapsed);

                        return null;
                    }

                    probe = null;
                    nextProbe = started.Elapsed + TimeSpan.FromMilliseconds(250);
                }

                if (display is not null)
                {
                    display.Draw(started.Elapsed);
                }
                else if (started.Elapsed >= nextNote)
                {
                    Console.Error.WriteLine(
                        $"tabard: still waiting for {address} ({started.Elapsed.TotalSeconds:0}s)."
                    );
                    nextNote += TimeSpan.FromSeconds(30);
                }

                cancel.Token.WaitHandle.WaitOne(
                    TimeSpan.FromMilliseconds(display is null ? 250 : 100)
                );
            }

            if (display is null)
                Console.Error.WriteLine($"tabard: gave up waiting for {address}.");
            else
                display.Fail($"gave up waiting for {address}.");

            return 130;
        }
        finally
        {
            display?.Dispose();
            Console.CancelKeyPress -= OnCancel;
        }
    }

    /// <summary>The profile's address to wait for, if it has one and something answers there.
    /// </summary>
    private static string? AlreadyAnswering(Profile profile)
    {
        if (WaitForOf(profile.Name) is not { } address)
            return null;

        try
        {
            return Answers(ParseAddress(address), CancellationToken.None) ? address : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>One connection attempt, a second at most - a host that drops packets rather than
    /// refusing would otherwise hold each try for the OS's whole connect timeout.</summary>
    private static bool Answers((string Host, int Port) target, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));

            using var client = new System.Net.Sockets.TcpClient();
            client
                .ConnectAsync(target.Host, target.Port, timeout.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Carried over by <see cref="ProfileStore.Rename"/>.</summary>
    public static void RenameProfile(string from, string to)
    {
        try
        {
            var entries = Read();
            if (!entries.Remove(from, out var entry))
                return;

            entries[to] = entry;
            Write(entries);
        }
        catch
        {
            // Housekeeping riding along with a rename that has already changed the disk.
        }
    }

    /// <summary>A profile created later under the same name must not inherit this one's command.
    /// </summary>
    public static void ForgetProfile(string profile)
    {
        try
        {
            var entries = Read();
            if (entries.Remove(profile))
                Write(entries);
        }
        catch
        {
            // As above.
        }
    }

    /// <summary>
    /// Starts the profile's companion, or returns null if it has none or it could not be started -
    /// in which case the agent still launches, with a warning. Its output goes to a log file: the
    /// agent owns the terminal, and anything written to it would land in the middle of the TUI.
    /// </summary>
    public static Running? Start(Profile profile)
    {
        if (Of(profile.Name) is not { } command)
            return null;

        // Already up - started by hand, or by another session of this profile. A second copy
        // would at best fail to bind, and at worst load a model twice before finding that out.
        // Not started means not stopped at exit either: that server is not tabard's.
        if (AlreadyAnswering(profile) is { } address)
        {
            Console.Error.WriteLine(
                $"tabard: {address} is already answering, so '{command}' was not started."
            );
            return null;
        }

        var log = LogFile(profile.Name);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);

            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
                : new ProcessStartInfo("/bin/sh")
                {
                    // SIGINT ignored, because ignored signals survive exec: a ^C that reaches the
                    // terminal's whole process group - while the agent is not in raw mode, say -
                    // is for the agent, not for the server behind it. tabard stops it with SIGTERM.
                    ArgumentList = { "-c", $"trap '' INT\n{command}" },
                };

            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.Environment[Harnesses.ConfigVariable(profile.Harness)] = Path.GetFullPath(
                profile.Dir
            );
            psi.Environment["TABARD_PROFILE"] = profile.Name;

            var writer = new StreamWriter(log, append: true) { AutoFlush = true };
            writer.WriteLine($"--- {DateTimeOffset.Now:u} tabard started: {command}");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Append(writer, e.Data);
            process.ErrorDataReceived += (_, e) => Append(writer, e.Data);

            if (!process.Start())
            {
                writer.Dispose();
                Console.Error.WriteLine($"tabard: could not start '{command}'.");
                return null;
            }

            // Nothing types into it: the terminal belongs to the agent.
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Console.Error.WriteLine(
                $"tabard: started '{command}' alongside - its output goes to {Paths.Pretty(log)}."
            );

            return new Running(command, log, process, writer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tabard: could not start '{command}': {ex.Message}");
            return null;
        }
    }

    private static void Append(StreamWriter writer, string? line)
    {
        if (line is null)
            return;

        lock (writer)
        {
            try
            {
                writer.WriteLine(line);
            }
            catch
            {
                // A log that cannot be written must not take the session down.
            }
        }
    }

    /// <summary>A companion that was started, and stops - along with anything it started - when
    /// disposed.</summary>
    internal sealed class Running(string command, string log, Process process, StreamWriter writer)
        : IDisposable
    {
        private int _stopped;

        public string Command => command;
        public string Log => log;

        /// <summary>
        /// The latest thing this run has said, while it is still running: the log's last line since
        /// tabard started it, with a progress bar's carriage returns and colour codes taken out.
        /// Null if it has said nothing yet or the log cannot be read.
        /// </summary>
        public string? LastLine()
        {
            try
            {
                using var stream = new FileStream(
                    log,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );

                // Only the end: a server that logs a lot must not make every frame slower.
                stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split('\n');

                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].StartsWith("--- ", StringComparison.Ordinal))
                        return null;

                    // A progress bar redraws with \r; what it last drew is after the last one.
                    var line = Plain(lines[i].TrimEnd('\r').Split('\r')[^1]).Trim();
                    if (line.Length > 0)
                        return line;
                }
            }
            catch
            {
                // Not there yet, or gone: nothing to show this frame.
            }

            return null;
        }

        /// <summary>Text without escape sequences or other control characters, safe to draw.
        /// </summary>
        internal static string Plain(string text)
        {
            var plain = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\x1b(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)?|.)",
                ""
            );
            return new string([.. plain.Where(c => !char.IsControl(c) || c == '\t')]).Replace(
                '\t',
                ' '
            );
        }

        /// <summary>The last lines this run wrote to the log, once its output has all arrived.
        /// </summary>
        public IReadOnlyList<string> Tail(int count)
        {
            try
            {
                // The parameterless wait also waits for the redirected output to drain.
                process.WaitForExit();

                lock (writer)
                {
                    // Shared for writing: the log is still open in this process, and Windows refuses a
                    // plain read of a file someone holds for writing.
                    using var stream = new FileStream(
                        log,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite
                    );
                    using var reader = new StreamReader(stream);
                    var lines = reader
                        .ReadToEnd()
                        .Split('\n')
                        .Select(l => l.TrimEnd('\r'))
                        .Where(l => l.Length > 0)
                        .ToArray();
                    var start =
                        Array.FindLastIndex(
                            lines,
                            l => l.StartsWith("--- ", StringComparison.Ordinal)
                        ) + 1;
                    return lines[Math.Max(start, lines.Length - count)..];
                }
            }
            catch
            {
                return [];
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            try
            {
                // Gone before anyone asked it to go: worth saying, because whatever the agent
                // expected it to be doing it was not doing.
                if (process.HasExited)
                {
                    if (process.ExitCode != 0)
                        Console.Error.WriteLine(
                            $"tabard: '{command}' exited early with code {process.ExitCode} - "
                                + $"see {Paths.Pretty(log)}."
                        );
                }
                else
                {
                    Stop(process);
                }
            }
            catch
            {
                // Best effort - there is nothing more useful to do on the way out.
            }
            finally
            {
                lock (writer)
                {
                    try
                    {
                        writer.WriteLine($"--- {DateTimeOffset.Now:u} tabard stopped it");
                        writer.Dispose();
                    }
                    catch
                    {
                        // As above.
                    }
                }

                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Asks the whole tree to stop, then kills what is left after <see cref="Grace"/>. The tree,
    /// because the shell the command runs under need not exec it: stopping only the shell would
    /// leave the server behind, still holding its port.
    /// </summary>
    private static void Stop(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            process.Kill(entireProcessTree: true);
            return;
        }

        var tree = Tree(process.Id);
        Signal("TERM", tree);

        if (process.WaitForExit(Grace) && Tree(process.Id, tree).Count == 0)
            return;

        // Killed by pid, not through the Process: once the shell is gone its children are
        // orphans .NET no longer counts as part of its tree.
        Signal("KILL", Tree(process.Id, tree));
        process.WaitForExit(TimeSpan.FromSeconds(1));
    }

    /// <summary>The pid and every descendant of it, read from 'ps'. With <paramref name="among"/>,
    /// only those of them still running - the orphans a stopped shell leaves behind.</summary>
    private static List<int> Tree(int root, IReadOnlyCollection<int>? among = null)
    {
        var children = new Dictionary<int, List<int>>();
        var alive = new HashSet<int>();

        try
        {
            var psi = new ProcessStartInfo("ps")
            {
                ArgumentList = { "-A", "-o", "pid=", "-o", "ppid=" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            using var ps = Process.Start(psi)!;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (
                    parts.Length == 2
                    && int.TryParse(parts[0], out var pid)
                    && int.TryParse(parts[1], out var ppid)
                )
                {
                    alive.Add(pid);
                    if (!children.TryGetValue(ppid, out var list))
                        children[ppid] = list = [];

                    list.Add(pid);
                }
            }
        }
        catch
        {
            // No 'ps': fall back to the root alone.
        }

        if (among is not null)
            return [.. among.Where(alive.Contains)];

        var tree = new List<int> { root };
        for (var i = 0; i < tree.Count; i++)
        {
            if (children.TryGetValue(tree[i], out var kids))
                tree.AddRange(kids.Where(k => !tree.Contains(k)));
        }

        return tree;
    }

    private static void Signal(string signal, IReadOnlyList<int> pids)
    {
        if (pids.Count == 0)
            return;

        try
        {
            var psi = new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add($"-{signal}");
            foreach (var pid in pids)
                psi.ArgumentList.Add(
                    pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );

            using var kill = Process.Start(psi)!;
            kill.StandardError.ReadToEnd(); // Already-gone pids complain; that is fine.
            kill.WaitForExit();
        }
        catch
        {
            // Nothing better to try.
        }
    }

    /// <summary>What tabard keeps for one profile. Either half can be set without the other: a
    /// profile can wait on a server it does not start itself.</summary>
    private sealed record Entry(string? Command, string? WaitFor);

    private static Dictionary<string, Entry> Read()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(Paths.CompanionsFile))
                return entries;

            using var stream = File.OpenRead(Paths.CompanionsFile);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
                return entries;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind is not JsonValueKind.Object)
                    continue;

                var parsed = new Entry(
                    String(entry.Value, "command"),
                    String(entry.Value, "waitFor")
                );
                if (parsed is not { Command: null, WaitFor: null })
                    entries[entry.Name] = parsed;
            }
        }
        catch
        {
            // Unreadable: start nothing alongside, which is how every profile began.
        }

        return entries;
    }

    /// <summary>Checked before GetString, which throws on anything else and would lose every entry
    /// after a hand-edited one.</summary>
    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static void Write(Dictionary<string, Entry> entries)
    {
        Directory.CreateDirectory(Paths.TabardRoot);

        var temp = $"{Paths.CompanionsFile}.{Environment.ProcessId}.tmp";

        try
        {
            using (
                var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)
            )
            using (
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })
            )
            {
                writer.WriteStartObject();

                foreach (
                    var (profile, entry) in entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                )
                {
                    writer.WriteStartObject(profile);

                    if (entry.Command is { } command)
                        writer.WriteString("command", command);

                    if (entry.WaitFor is { } waitFor)
                        writer.WriteString("waitFor", waitFor);

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            File.Move(temp, Paths.CompanionsFile, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // A leftover .tmp is inert - nothing reads it.
            }

            throw;
        }
    }
}
