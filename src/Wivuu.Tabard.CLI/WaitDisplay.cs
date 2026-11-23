using System.Text;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// What the terminal shows while a launch waits for its port: a spinner and the time so far, a
/// bar sweeping back and forth (nobody knows how long a server takes, so it does not pretend to
/// fill), and the latest line from the command alongside, so a model loading or a migration running
/// is visible rather than a silent pause.
///
/// <code>
///   ⠹ Waiting for strata on 127.0.0.1:8080   12s   ^C to give up
///     ───────────━━━━━━━━──────────────────────
///     › loading experts 34/64
/// </code>
///
/// Drawn on stderr with ANSI sequences, redrawn in place, and only when stderr is a terminal that
/// understands them; otherwise <see cref="Create"/> returns null and the caller prints plain lines.
/// </summary>
internal sealed class WaitDisplay : IDisposable
{
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";
    private const string Bold = "\x1b[1m";
    private const string Cyan = "\x1b[36m";
    private const string Green = "\x1b[32m";
    private const string Red = "\x1b[31m";

    private static readonly string[] UnicodeSpinner =
    [
        "⠋",
        "⠙",
        "⠹",
        "⠸",
        "⠼",
        "⠴",
        "⠦",
        "⠧",
        "⠇",
        "⠏",
    ];
    private static readonly string[] AsciiSpinner = ["|", "/", "-", "\\"];

    private const int BarWidth = 40;
    private const int Highlight = 8;

    private readonly string _what;
    private readonly string _address;
    private readonly Func<string?> _latest;
    private readonly bool _unicode = Term.Unicode;
    private int _frame;
    private int _drawn;
    private string? _line;
    private bool _done;

    private WaitDisplay(string what, string address, Func<string?> latest)
    {
        _what = what;
        _address = address;
        _latest = latest;
    }

    /// <summary>A display for waiting on <paramref name="address"/>, or null where a redrawn one
    /// would only make a mess: output redirected, a dumb terminal, or Windows' console, which does
    /// not take these sequences on stderr unless asked.</summary>
    public static WaitDisplay? Create(string address, Companions.Running? running)
    {
        if (
            Console.IsErrorRedirected
            || OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable("TERM") is null or "" or "dumb"
            || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }
        )
        {
            return null;
        }

        return new WaitDisplay(
            Name(running?.Command) ?? "",
            address,
            () => running?.LastLine()
        ).Start();
    }

    /// <summary>What the command alongside is called in a sentence: the program it starts,
    /// 'strata' for 'strata serve --port 8080'.</summary>
    internal static string? Name(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var first = command.Trim().Split(' ', 2)[0].Trim('"', '\'');
        var name = Path.GetFileName(first);
        return name.Length > 0 ? name : null;
    }

    /// <summary>Draws the next frame. Cheap enough to call ten times a second; the log is only
    /// read every few frames.</summary>
    public void Draw(TimeSpan elapsed)
    {
        if (_done)
            return;

        if (_frame % 3 == 0)
            _line = _latest() ?? _line;

        var width = Term.WindowWidth();
        var spinner = _unicode ? UnicodeSpinner : AsciiSpinner;
        var title =
            _what.Length > 0 ? $"Waiting for {_what} on {_address}" : $"Waiting for {_address}";
        var time = Seconds(elapsed);
        const string hint = "^C to give up";

        var lines = new List<string>(3)
        {
            Fit(
                width,
                ($"  {spinner[_frame % spinner.Length]} ", Cyan),
                (title, Bold),
                ($"   {time}", ""),
                ($"   {hint}", Dim)
            ),
            "    " + Bar(Math.Min(BarWidth, Math.Max(10, width - 6))),
        };

        if (_line is { } latest)
            lines.Add(Fit(width, ($"    {(_unicode ? "›" : ">")} ", Dim), (latest, Dim)));

        var frame = new StringBuilder();
        if (_drawn > 1)
            frame.Append($"\x1b[{_drawn - 1}A");

        frame.Append('\r');
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                frame.Append('\n');
            frame.Append("\x1b[2K").Append(lines[i]);
        }

        // A log line that went away leaves a row below; clear it rather than leave it stale.
        if (_drawn > lines.Count)
            frame.Append("\x1b[J");

        Write(frame.ToString());
        _drawn = lines.Count;
        _frame++;
    }

    /// <summary>Replaces the display with one line saying the port answered.</summary>
    public void Succeed(TimeSpan elapsed) =>
        Finish(
            $"  {Green}{(_unicode ? "✓" : "ok")}{Reset} {(_what.Length > 0 ? $"{_what} is up on " : "")}"
                + $"{_address}{Dim} after {Seconds(elapsed)}{Reset}"
        );

    /// <summary>Replaces the display with one line saying why the launch is not going ahead.
    /// </summary>
    public void Fail(string message) => Finish($"  {Red}{(_unicode ? "✗" : "x")}{Reset} {message}");

    /// <summary>Takes the display off the screen, leaving the cursor where it started.</summary>
    public void Clear() => Finish(null);

    private void Finish(string? line)
    {
        if (_done)
            return;

        _done = true;

        var text = new StringBuilder();
        if (_drawn > 1)
            text.Append($"\x1b[{_drawn - 1}A");
        text.Append("\r\x1b[J");
        if (line is not null)
            text.Append(line).Append('\n');

        Write(text.ToString());
        Write("\x1b[?25h");
    }

    public void Dispose() => Clear();

    /// <summary>The bar: a dim track with a bright stretch bouncing from end to end.</summary>
    internal string Bar(int width)
    {
        var travel = Math.Max(1, width - Highlight);
        var step = _frame % (travel * 2);
        var at = step <= travel ? step : travel * 2 - step;

        var track = _unicode ? "─" : "-";
        var lit = _unicode ? "━" : "=";

        return Dim
            + string.Concat(Enumerable.Repeat(track, at))
            + Reset
            + Cyan
            + string.Concat(Enumerable.Repeat(lit, Highlight))
            + Reset
            + Dim
            + string.Concat(Enumerable.Repeat(track, Math.Max(0, width - at - Highlight)))
            + Reset;
    }

    /// <summary>Joins styled pieces, cutting the text - never a sequence - at the window's edge.
    /// </summary>
    private static string Fit(int width, params (string Text, string Style)[] parts)
    {
        var line = new StringBuilder();
        var room = width;

        foreach (var (text, style) in parts)
        {
            if (room <= 0)
                break;

            var shown = Term.Clip(text, room);
            room -= shown.Length;
            line.Append(style).Append(shown).Append(style.Length > 0 ? Reset : "");
        }

        return line.ToString();
    }

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"
            : $"{elapsed.TotalSeconds:0}s";

    private static void Write(string text)
    {
        try
        {
            Console.Error.Write(text);
            Console.Error.Flush();
        }
        catch
        {
            // A terminal that went away mid-wait: the wait itself goes on regardless.
        }
    }

    /// <summary>Hides the cursor so it does not flicker across the frame.</summary>
    public WaitDisplay Start()
    {
        Write("\x1b[?25l");
        return this;
    }
}
