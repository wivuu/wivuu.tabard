using System.Text;

namespace Wivuu.Tabard.Cli;

internal static class Picker
{
    private const int ChromeLines = 5; // header, blank, blank, help, status
    private const int NameWidth = 18;

    private const int HeaderLines = 2; // rows start below the title and its blank line
    private const int NameColumn = 5; // ' ' + slot + ' ' + marker + ' '

    /// <summary>How many rows get a digit to launch them. Ten would need '0', which reads as a
    /// zeroth row rather than a tenth.</summary>
    private const int Slots = 9;

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
        var reordering = false; // True while up/down moves the profile instead of the cursor.
        StringBuilder? draft = null; // The name being typed, or null when not renaming.
        string? status = null;
        var notes = new List<string>();

        Profile? Leave(Profile? chosen)
        {
            Finish(top, frameHeight);
            foreach (var note in notes)
                Console.Error.WriteLine($"tabard: {note}");

            return chosen;
        }

        // Written after every move rather than on the way out of the mode, so a ^C - or a terminal
        // that goes away - cannot lose an arrangement the user has already watched happen.
        string? Save()
        {
            try
            {
                using var guard = ProfileStore.AcquireLock();
                ProfileStore.SetOrder(profiles.Select(p => p.Name));
                return null;
            }
            catch (Exception ex)
            {
                notes.Add($"could not save the profile order: {ex.Message}");
                return "could not save the order - see below";
            }
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
                Render(
                    profiles,
                    index,
                    offset,
                    rows,
                    armed,
                    reordering,
                    draft?.ToString(),
                    status,
                    top,
                    frameHeight
                );

                if (draft is null)
                    Term.HideCursor();
                else
                    Caret(top + HeaderLines + index - offset, NameColumn + draft.Length);

                var key = Term.ReadKey();
                var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
                status = null;

                if (key.Key == ConsoleKey.C && control)
                    return Leave(null);

                // Handled before the keys below because while a name is being typed every printable
                // key is text, including the ones that are commands the rest of the time.
                if (draft is not null)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            var subject = profiles[index];
                            try
                            {
                                var result = ProfileStore.Rename(subject, draft.ToString().Trim());

                                notes.AddRange(result.Warnings);
                                profiles[index] = result.Profile;
                                status = string.Equals(
                                    result.Profile.Name,
                                    subject.Name,
                                    StringComparison.Ordinal
                                )
                                    ? null
                                    : $"renamed '{subject.Name}' to '{result.Profile.Name}'";

                                draft = null;
                            }
                            catch (Exception ex)
                            {
                                // Stay in the field: a rejected name is one to correct, not retype.
                                status = ex.Message;
                            }

                            break;

                        case ConsoleKey.Escape:
                            if (Term.SwallowSplitEscape())
                                break;

                            draft = null;
                            break;

                        case ConsoleKey.Backspace:
                            if (draft.Length > 0)
                                draft.Length--;

                            break;

                        case ConsoleKey.U when control:
                            draft.Clear();
                            break;

                        default:
                            if (!control && !char.IsControl(key.KeyChar))
                                draft.Append(key.KeyChar);

                            break;
                    }

                    continue;
                }

                // Modal for the same reason the rename field is: while up/down carries the
                // highlighted profile, it cannot also move the cursor, and nothing else here
                // makes sense half way through rearranging the list.
                if (reordering)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow or ConsoleKey.K:
                            index = MoveTo(profiles, index, index - 1);
                            status = Save();
                            break;

                        case ConsoleKey.DownArrow
                        or ConsoleKey.J:
                            index = MoveTo(profiles, index, index + 1);
                            status = Save();
                            break;

                        case ConsoleKey.Escape:
                            if (Term.SwallowSplitEscape())
                                break;

                            reordering = false;
                            break;

                        case ConsoleKey.Enter
                        or ConsoleKey.O
                        or ConsoleKey.Q:
                            reordering = false;
                            break;
                    }

                    continue;
                }

                // A digit launches, so it is handled ahead of the switch rather than as a case
                // that has to be kept out of the disarm rule. KeyChar rather than ConsoleKey.D1
                // covers the number row and the keypad without depending on the terminal's
                // mapping of either, and shift+1 is '!' so there is no false positive.
                if (!control && key.KeyChar is >= '1' and <= '9')
                {
                    var slot = key.KeyChar - '1';
                    if (slot < profiles.Count)
                        return Leave(profiles[slot]);

                    armed = -1; // Past the end of the list - the 'any other key disarms' rule holds.
                    continue;
                }

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

                    case ConsoleKey.R:
                        if (profiles.Count == 0)
                            break;

                        // Seeded with the current name, so a small correction is a small edit.
                        draft = new StringBuilder(profiles[index].Name);
                        armed = -1;
                        break;

                    case ConsoleKey.O:
                        // A mode with nothing to move in it is worse than no mode. Reachable
                        // even though the picker needs two profiles to open: a delete can leave
                        // one behind mid-session.
                        if (profiles.Count < 2)
                            break;

                        reordering = true;
                        armed = -1;
                        break;

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

    /// <summary>
    /// The launch key printed beside a row, or blanks past the ninth - there is no key for those.
    /// The absolute position, not the visible one: a number that changed as the list scrolled
    /// would be worse than no number, because the whole point of it is muscle memory.
    /// </summary>
    internal static string Gutter(int index) => index < Slots ? $"{index + 1} " : "  ";

    /// <summary>
    /// Moves an item to another position and returns where it ended up. Clamped rather than
    /// wrapped: the cursor wraps because that is free, but rotating the list by one renumbers
    /// every row, which is exactly what the numbers exist not to do.
    /// </summary>
    internal static int MoveTo<T>(IList<T> items, int from, int to)
    {
        to = Math.Clamp(to, 0, items.Count - 1);
        if (to == from)
            return from;

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        return to;
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

    /// <summary>Parks the terminal cursor at the end of the name being typed, so typing looks like
    /// typing rather than characters appearing beside a hidden cursor.</summary>
    private static void Caret(int row, int column)
    {
        try
        {
            Console.SetCursorPosition(Math.Min(column, Term.WindowWidth() - 1), row);
            Console.CursorVisible = true;
        }
        catch
        {
            // Window shrank under us; the next keystroke redraws.
        }
    }

    private static void Render(
        IReadOnlyList<Profile> profiles,
        int index,
        int offset,
        int rows,
        int armed,
        bool reordering,
        string? draft,
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

            // Every row form carries the gutter, so a row never shifts sideways on its way into
            // being renamed or armed - and NameColumn stays the truth for the caret.
            var gutter = $" {Gutter(i)}{marker} ";

            // The field takes the whole row: a name being typed can outgrow the name column, and
            // what the profile currently holds is not what is being edited.
            if (draft is not null && selected)
            {
                lines.Add(($"{gutter}{draft}", ConsoleColor.Yellow));
                continue;
            }

            if (i == armed)
            {
                lines.Add(($"{gutter}{name}press x again to delete", ConsoleColor.Red));
                continue;
            }

            // Yellow is this frame's 'in progress' colour already, and it is the only thing
            // standing between pressing 'o' by accident and wondering why the list moved.
            ConsoleColor? colour = selected
                ? reordering
                    ? ConsoleColor.Yellow
                    : ConsoleColor.Cyan
                : null;

            lines.Add(($"{gutter}{name}{profile.Describe()}", colour));
        }

        var hidden = profiles.Count - rows;

        // 'q quit' rather than 'esc quit' buys the two columns that keep this inside an
        // 80-column window; esc still works, and the frame is no use if the way out is what
        // gets clipped off the end.
        var help =
            draft is not null ? "  type a new name   enter save   esc cancel"
            : reordering ? "  up/down move the profile   o or enter done"
            : "  up/down move   enter/1-9 launch   o reorder   r rename   x x delete   q quit";

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
