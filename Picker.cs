namespace Envy.Cli;

internal static class Picker
{
    private const int ChromeLines = 5; // header, blank, blank, help, status

    /// <summary>Returns the chosen profile, or null if the user quit or deleted everything.</summary>
    public static Profile? Show(List<Profile> profiles)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return ShowPlain(profiles);

        var frameHeight = profiles.Count + ChromeLines;

        // Reserve the frame up front so the terminal scrolls once, then draw in
        // place from a stable origin.
        for (var i = 0; i < frameHeight; i++)
            Console.WriteLine();

        var top = Math.Max(0, Console.CursorTop - frameHeight);
        var index = 0;
        var armed = -1;
        string? status = null;

        Console.CursorVisible = false;
        try
        {
            while (true)
            {
                Render(profiles, index, armed, status, top, frameHeight);

                var key = Console.ReadKey(intercept: true);
                status = null;

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        index = profiles.Count == 0 ? 0 : (index - 1 + profiles.Count) % profiles.Count;
                        armed = -1;
                        break;

                    case ConsoleKey.DownArrow or ConsoleKey.J:
                        index = profiles.Count == 0 ? 0 : (index + 1) % profiles.Count;
                        armed = -1;
                        break;

                    case ConsoleKey.Enter:
                        if (profiles.Count == 0)
                            return null;
                        return profiles[index];

                    case ConsoleKey.X:
                        if (profiles.Count == 0)
                            break;

                        if (armed == index)
                        {
                            var doomed = profiles[index];
                            try
                            {
                                ProfileStore.Delete(doomed);
                                profiles.RemoveAt(index);
                                status = $"deleted '{doomed.Name}'";
                            }
                            catch (Exception ex)
                            {
                                status = $"could not delete '{doomed.Name}': {ex.Message}";
                            }

                            armed = -1;
                            if (profiles.Count == 0)
                            {
                                Finish(top, frameHeight);
                                Console.WriteLine("envy: no profiles left. Run 'envy add <name>' to create one.");
                                return null;
                            }

                            index = Math.Clamp(index, 0, profiles.Count - 1);
                        }
                        else
                        {
                            armed = index;
                        }

                        break;

                    case ConsoleKey.Escape or ConsoleKey.Q:
                        Finish(top, frameHeight);
                        return null;

                    default:
                        armed = -1;
                        break;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
            Console.ResetColor();
        }
    }

    private static void Render(
        IReadOnlyList<Profile> profiles, int index, int armed, string? status, int top, int frameHeight)
    {
        var lines = new List<(string Text, ConsoleColor? Color)>(frameHeight)
        {
            ("  Select a Claude profile", ConsoleColor.White),
            ("", null),
        };

        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var selected = i == index;
            var marker = selected ? ">" : " ";

            if (i == armed)
            {
                lines.Add(($" {marker} {profile.Name,-18}press x again to delete", ConsoleColor.Red));
                continue;
            }

            lines.Add((
                $" {marker} {profile.Name,-18}{profile.Describe()}",
                selected ? ConsoleColor.Cyan : null));
        }

        lines.Add(("", null));
        lines.Add(("  up/down move   enter launch   x x delete   esc quit", ConsoleColor.DarkGray));
        lines.Add((status is null ? "" : "  " + status, ConsoleColor.Yellow));

        // Always paint the full reserved frame so deleted rows are cleared.
        while (lines.Count < frameHeight)
            lines.Add(("", null));

        var width = Math.Max(20, Console.WindowWidth - 1);

        for (var i = 0; i < frameHeight; i++)
        {
            Console.SetCursorPosition(0, top + i);

            var (text, color) = lines[i];
            if (color is { } c)
                Console.ForegroundColor = c;

            var padded = text.PadRight(width);
            Console.Write(padded.Length > width ? padded[..width] : padded);
            Console.ResetColor();
        }
    }

    private static void Finish(int top, int frameHeight)
    {
        try
        {
            Console.SetCursorPosition(0, Math.Min(top + frameHeight, Console.BufferHeight - 1));
        }
        catch
        {
            // Terminal resized under us; not worth failing over.
        }
    }

    /// <summary>Non-interactive fallback for pipes and CI.</summary>
    private static Profile? ShowPlain(List<Profile> profiles)
    {
        Console.Error.WriteLine("envy: profiles available:");
        foreach (var profile in profiles)
            Console.Error.WriteLine($"  {profile.Name}  ({profile.Describe()})");

        Console.Error.WriteLine("envy: not a terminal - pick one with 'envy use <name>'.");
        return null;
    }
}
