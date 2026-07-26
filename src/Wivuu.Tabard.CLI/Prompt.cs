using System.Text;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// The prompts the wizard is built from, drawn the same way as <see cref="Picker"/>: reserve a frame
/// up front so the terminal scrolls once, then paint it in place. Every one of these returns null
/// when the user escapes, and a null must abandon the command - a cancelled prompt may still have a
/// keystroke in flight (see <see cref="Term.SwallowSplitEscape"/>).
/// </summary>
internal static class Prompt
{
    private const int SelectChrome = 5; // title, blank, blank, filter, help

    /// <summary>
    /// An arrow-key list with type-to-filter. The filter is not a nicety: the OpenRouter catalog is
    /// a few hundred models and scrolling to one of them is not a realistic way to choose.
    /// </summary>
    public static T? Select<T>(
        string title,
        IReadOnlyList<T> items,
        Func<T, string> label,
        Func<T, string>? detail = null,
        int start = 0
    )
        where T : class
    {
        if (!Term.Interactive || items.Count == 0)
            return null;

        // Same reasoning as the picker: below this the frame collapses onto one line and enter would
        // pick something nobody saw. Say so - a silent null here reads as an unexplained cancel.
        var maxRows = Term.WindowHeight() - SelectChrome - 1;
        if (maxRows < 1)
        {
            Console.Error.WriteLine(
                "tabard: this window is too short to draw a menu. Make it taller, or pass the "
                    + "choice as a flag (see 'tabard openrouter help')."
            );
            return null;
        }

        var width = Math.Min(44, items.Max(item => label(item).Length) + 2);
        var rows = Math.Min(items.Count, maxRows);
        var frameHeight = rows + SelectChrome;

        var matches = new List<T>(items);
        var filter = new StringBuilder();
        var index = Math.Clamp(start, 0, items.Count - 1);
        var offset = 0;

        for (var i = 0; i < frameHeight; i++)
            Console.WriteLine();

        var top = Math.Max(0, Console.CursorTop - frameHeight);

        void Restore(object? sender, ConsoleCancelEventArgs e) => Term.ShowCursor();
        Console.CancelKeyPress += Restore;

        try
        {
            Term.HideCursor();

            while (true)
            {
                offset = Scroll(offset, index, rows, matches.Count);
                Render(
                    title,
                    matches,
                    label,
                    detail,
                    width,
                    index,
                    offset,
                    rows,
                    filter.ToString(),
                    items.Count,
                    top,
                    frameHeight
                );

                var key = Term.ReadKey();
                var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);

                if (control && key.Key is ConsoleKey.C)
                    return null;

                // Ctrl+N/P move as well as the arrows, because every printable key is filter input.
                var down = key.Key is ConsoleKey.DownArrow || (control && key.Key is ConsoleKey.N);
                var up = key.Key is ConsoleKey.UpArrow || (control && key.Key is ConsoleKey.P);

                if ((up || down) && matches.Count > 0)
                {
                    index = (index + (down ? 1 : -1) + matches.Count) % matches.Count;
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Enter when matches.Count > 0:
                        return matches[index];

                    case ConsoleKey.Escape:
                        if (Term.SwallowSplitEscape())
                            continue;

                        return null;

                    case ConsoleKey.Backspace:
                        if (filter.Length == 0)
                            continue;

                        filter.Length--;
                        break;

                    case ConsoleKey.U when control:
                        if (filter.Length == 0)
                            continue;

                        filter.Clear();
                        break;

                    default:
                        if (control || char.IsControl(key.KeyChar))
                            continue;

                        filter.Append(key.KeyChar);
                        break;
                }

                // The filter changed, so the old index means nothing.
                matches = Match(items, label, detail, filter.ToString());
                index = 0;
                offset = 0;
            }
        }
        finally
        {
            Console.CancelKeyPress -= Restore;
            Term.ShowCursor();
            Finish(top, frameHeight);
        }
    }

    /// <summary>
    /// A single line of input. <paramref name="secret"/> echoes stars - it is what the API key goes
    /// through, and a key left on screen ends up in a scrollback or a screenshot.
    /// </summary>
    public static string? Text(string label, string? initial = null, bool secret = false)
    {
        if (!Term.Interactive)
            return null;

        var value = new StringBuilder(initial ?? "");

        void Restore(object? sender, ConsoleCancelEventArgs e) => Term.ShowCursor();
        Console.CancelKeyPress += Restore;

        try
        {
            while (true)
            {
                Paint(label, secret ? new string('*', value.Length) : value.ToString());

                var key = Term.ReadKey();
                var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);

                if (control && key.Key is ConsoleKey.C)
                {
                    Console.WriteLine();
                    return null;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        return value.ToString().Trim();

                    case ConsoleKey.Escape:
                        Console.WriteLine();
                        return null;

                    case ConsoleKey.Backspace:
                        if (value.Length > 0)
                            value.Length--;

                        break;

                    case ConsoleKey.U when control:
                        value.Clear();
                        break;

                    default:
                        if (!control && !char.IsControl(key.KeyChar))
                            value.Append(key.KeyChar);

                        break;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= Restore;
            Term.ShowCursor();
        }
    }

    /// <summary>Null if the user escaped, so the caller can tell 'no' from 'never mind'.</summary>
    public static bool? Confirm(string question, bool preset = true)
    {
        if (!Term.Interactive)
            return null;

        Console.Write($"  {question} {(preset ? "[Y/n]" : "[y/N]")} ");

        while (true)
        {
            var key = Term.ReadKey();

            if (key.Key is ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.WriteLine();
                return null;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine(preset ? "yes" : "no");
                    return preset;

                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;
            }

            switch (char.ToLowerInvariant(key.KeyChar))
            {
                case 'y':
                    Console.WriteLine("yes");
                    return true;

                case 'n':
                    Console.WriteLine("no");
                    return false;
            }
        }
    }

    /// <summary>Every whitespace-separated term has to appear somewhere in the row, so 'claude opus'
    /// narrows the way people expect regardless of the order they typed it.</summary>
    private static List<T> Match<T>(
        IReadOnlyList<T> items,
        Func<T, string> label,
        Func<T, string>? detail,
        string filter
    )
    {
        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return [.. items];

        var matches = new List<T>();

        foreach (var item in items)
        {
            var haystack = detail is null ? label(item) : $"{label(item)} {detail(item)}";

            if (terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)))
                matches.Add(item);
        }

        return matches;
    }

    private static int Scroll(int offset, int index, int rows, int count)
    {
        if (index < offset)
            offset = index;
        else if (index >= offset + rows)
            offset = index - rows + 1;

        return Math.Clamp(offset, 0, Math.Max(0, count - rows));
    }

    private static void Render<T>(
        string title,
        IReadOnlyList<T> matches,
        Func<T, string> label,
        Func<T, string>? detail,
        int width,
        int index,
        int offset,
        int rows,
        string filter,
        int total,
        int top,
        int frameHeight
    )
    {
        var lines = new List<(string Text, ConsoleColor? Color)>(frameHeight)
        {
            ("  " + title, ConsoleColor.White),
            ("", null),
        };

        for (var i = offset; i < matches.Count && i < offset + rows; i++)
        {
            var selected = i == index;
            var marker = selected ? ">" : " ";
            var name = label(matches[i]);
            var text = detail is null
                ? $" {marker} {name}"
                : $" {marker} {Term.Clip(name, width - 1).PadRight(width)}{detail(matches[i])}";

            lines.Add((text, selected ? ConsoleColor.Cyan : null));
        }

        if (matches.Count == 0)
            lines.Add(("    nothing matches", ConsoleColor.DarkGray));

        var hidden = matches.Count - rows;
        var help = "  up/down move   enter select   esc cancel";

        lines.Add(("", null));
        lines.Add(
            (
                filter.Length == 0
                    ? "  type to filter"
                    : $"  filter: {filter}   ({matches.Count} of {total})",
                ConsoleColor.Yellow
            )
        );
        lines.Add((hidden > 0 ? $"{help}   ({hidden} more)" : help, ConsoleColor.DarkGray));

        // Always paint the full reserved frame so filtered-out rows are cleared.
        while (lines.Count < frameHeight)
            lines.Add(("", null));

        var windowWidth = Term.WindowWidth();

        for (var i = 0; i < frameHeight; i++)
        {
            try
            {
                Console.SetCursorPosition(0, top + i);
            }
            catch
            {
                break; // Window shrank under us; the next keystroke redraws.
            }

            var (text, color) = lines[i];
            if (color is { } c)
                Console.ForegroundColor = c;

            Console.Write(Term.Clip(text, windowWidth).PadRight(windowWidth));
            Console.ResetColor();
        }
    }

    /// <summary>Redraws one line in place. Cheaper than a frame, and text entry only ever needs one.</summary>
    private static void Paint(string label, string value)
    {
        var width = Term.WindowWidth();
        var line = Term.Clip($"  {label}: {value}", width);

        Console.Write('\r');
        Console.Write(new string(' ', width));
        Console.Write('\r');
        Console.Write(line);
    }

    private static void Finish(int top, int frameHeight)
    {
        try
        {
            Console.SetCursorPosition(0, Math.Min(top + frameHeight, Console.BufferHeight - 1));
            Console.WriteLine();
        }
        catch
        {
            // Terminal resized under us; not worth failing over.
        }
    }
}
