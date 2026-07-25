namespace Wivuu.Tabard.Cli;

internal static class Picker
{
    private const int ChromeLines = 5; // header, blank, blank, help, status
    private const int NameWidth = 18;

    /// <summary>Returns the chosen profile, or null if the user quit or deleted everything.</summary>
    public static Profile? Show(List<Profile> profiles)
    {
        if (!Term.Interactive)
            return ShowPlain(profiles);

        // Below this the frame cannot be drawn in place at all: rows would collapse onto the last
        // line and enter would launch an account nobody saw. Listing them is the honest fallback.
        var maxRows = Term.WindowHeight() - ChromeLines - 1;
        if (maxRows < 1)
            return ShowPlain(profiles);

        var rows = Math.Min(profiles.Count, maxRows);
        var frameHeight = rows + ChromeLines;

        // Reserve the frame up front so the terminal scrolls once, then draw in
        // place from a stable origin.
        for (var i = 0; i < frameHeight; i++)
            Console.WriteLine();

        var top = Math.Max(0, Console.CursorTop - frameHeight);
        var index = 0;
        var offset = 0;
        var armed = -1;
        string? status = null;
        var notes = new List<string>();

        Profile? Leave(Profile? chosen)
        {
            Finish(top, frameHeight);
            foreach (var note in notes)
                Console.Error.WriteLine($"tabard: {note}");

            return chosen;
        }

        // The finally below cannot run if ^C terminates us, so restore the cursor from the
        // handler too - otherwise the user's terminal is left with no cursor at all.
        void Restore(object? sender, ConsoleCancelEventArgs e) => Term.ShowCursor();
        Console.CancelKeyPress += Restore;

        try
        {
            Console.CursorVisible = false;

            while (true)
            {
                offset = Scroll(offset, index, rows, profiles.Count);
                Render(profiles, index, offset, rows, armed, status, top, frameHeight);

                var key = Console.ReadKey(intercept: true);
                status = null;

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    return Leave(null);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        index =
                            profiles.Count == 0 ? 0 : (index - 1 + profiles.Count) % profiles.Count;
                        armed = -1;
                        break;

                    case ConsoleKey.DownArrow
                    or ConsoleKey.J:
                        index = profiles.Count == 0 ? 0 : (index + 1) % profiles.Count;
                        armed = -1;
                        break;

                    case ConsoleKey.Enter:
                        return profiles.Count == 0 ? Leave(null) : Leave(profiles[index]);

                    case ConsoleKey.X:
                        if (profiles.Count == 0)
                            break;

                        if (armed == index)
                        {
                            var doomed = profiles[index];
                            try
                            {
                                notes.AddRange(ProfileStore.Delete(doomed));
                                profiles.RemoveAt(index);
                                status = $"deleted '{doomed.Name}'";
                            }
                            catch (Exception ex)
                            {
                                status = $"could not delete '{doomed.Name}' - see below";
                                notes.Add($"could not delete '{doomed.Name}': {ex.Message}");
                            }

                            armed = -1;
                            if (profiles.Count == 0)
                            {
                                notes.Add(
                                    "no profiles left. Run 'tabard add <name>' to create one."
                                );
                                return Leave(null);
                            }

                            index = Math.Clamp(index, 0, profiles.Count - 1);
                        }
                        else
                        {
                            armed = index;
                        }

                        break;

                    case ConsoleKey.Escape:
                        if (Term.SwallowSplitEscape())
                            break;

                        return Leave(null);

                    case ConsoleKey.Q:
                        return Leave(null);

                    default:
                        armed = -1;
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

    /// <summary>Keeps the selected row inside the visible window.</summary>
    private static int Scroll(int offset, int index, int rows, int count)
    {
        if (index < offset)
            offset = index;
        else if (index >= offset + rows)
            offset = index - rows + 1;

        return Math.Clamp(offset, 0, Math.Max(0, count - rows));
    }

    private static void Render(
        IReadOnlyList<Profile> profiles,
        int index,
        int offset,
        int rows,
        int armed,
        string? status,
        int top,
        int frameHeight
    )
    {
        var lines = new List<(string Text, ConsoleColor? Color)>(frameHeight)
        {
            ("  Select a Claude profile", ConsoleColor.White),
            ("", null),
        };

        for (var i = offset; i < profiles.Count && i < offset + rows; i++)
        {
            var profile = profiles[i];
            var selected = i == index;
            var name = Term.Clip(profile.Name, NameWidth - 1).PadRight(NameWidth);
            var marker = selected ? ">" : " ";

            if (i == armed)
            {
                lines.Add(($" {marker} {name}press x again to delete", ConsoleColor.Red));
                continue;
            }

            lines.Add(
                ($" {marker} {name}{profile.Describe()}", selected ? ConsoleColor.Cyan : null)
            );
        }

        var hidden = profiles.Count - rows;
        var help = "  up/down move   enter launch   x x delete   esc quit";

        lines.Add(("", null));
        lines.Add((hidden > 0 ? $"{help}   ({hidden} more)" : help, ConsoleColor.DarkGray));
        lines.Add((status is null ? "" : "  " + status, ConsoleColor.Yellow));

        // Always paint the full reserved frame so deleted rows are cleared.
        while (lines.Count < frameHeight)
            lines.Add(("", null));

        var width = Term.WindowWidth();

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

            Console.Write(Term.Clip(text, width).PadRight(width));
            Console.ResetColor();
        }
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

    /// <summary>Non-interactive fallback for pipes and CI.</summary>
    private static Profile? ShowPlain(List<Profile> profiles)
    {
        Console.Error.WriteLine("tabard: profiles available:");
        foreach (var profile in profiles)
            Console.Error.WriteLine($"  {profile.Name}  ({profile.Describe()})");

        Console.Error.WriteLine("tabard: not a terminal - pick one with 'tabard use <name>'.");
        return null;
    }
}
