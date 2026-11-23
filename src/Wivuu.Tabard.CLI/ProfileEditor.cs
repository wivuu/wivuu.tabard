using System.Text;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// The picker's 'e': every setting tabard keeps for one profile, on one screen, drawn in the
/// picker's own frame. Each change is saved as it is made, like the picker's order and sandbox
/// toggle - there is no 'save' step to forget.
/// </summary>
internal static class ProfileEditor
{
    internal enum Field
    {
        Name,
        Sandbox,
        Image,
        Companion,
        WaitFor,
    }

    private const int ChromeLines = 6; // title, blank, blank, hint, help, status
    private const int HeaderLines = 2;
    private const int Indent = 4; // "  > "
    private const int LabelWidth = 16;

    /// <summary>What can be set on this profile. A pi profile's sandboxes are made from tabard's
    /// own pi template, so it has no image to choose.</summary>
    internal static IReadOnlyList<Field> Fields(Profile profile) =>
        profile.Harness is Harness.Claude
            ? [Field.Name, Field.Sandbox, Field.Image, Field.Companion, Field.WaitFor]
            : [Field.Name, Field.Sandbox, Field.Companion, Field.WaitFor];

    /// <summary>The frame height the editor needs for this profile.</summary>
    internal static int Height(Profile profile) => Fields(profile).Count + ChromeLines;

    internal static string Label(Field field) =>
        field switch
        {
            Field.Name => "Name",
            Field.Sandbox => "Sandbox",
            Field.Image => "Sandbox image",
            Field.Companion => "Run alongside",
            Field.WaitFor => "Wait for port",
            _ => field.ToString(),
        };

    internal static string Hint(Field field, Profile profile) =>
        field switch
        {
            Field.Name => "Also its directory under ~/.tabard/profiles.",
            Field.Sandbox => "Launch the agent inside a Docker sandbox (sbx).",
            Field.Image => "What new sandboxes are made from. Blank for sbx's default.",
            Field.Companion =>
                $"Runs while the agent does. Output: {Paths.Pretty(Companions.LogFile(profile.Name))}",
            Field.WaitFor => "Launch the agent once this answers: 8080, or host:8080.",
            _ => "",
        };

    internal static string Help(bool typing, bool onToggle) =>
        typing ? "  type a value   enter save   esc cancel   ctrl+u clear"
        : onToggle ? "  up/down move   enter/space toggle   esc back"
        : "  up/down move   enter change   esc back";

    private static string Value(Field field, Profile profile) =>
        field switch
        {
            Field.Name => profile.Name,
            Field.Sandbox => Sandboxing.IsOn(profile.Name) ? "on" : "off",
            Field.Image => Sbx.ImageOf(profile.Name) ?? "",
            Field.Companion => Companions.Of(profile.Name) ?? "",
            Field.WaitFor => Companions.WaitForOf(profile.Name) ?? "",
            _ => "",
        };

    private static string Placeholder(Field field) =>
        field switch
        {
            Field.Image => "(sbx default)",
            Field.Companion => "(nothing)",
            Field.WaitFor => "(launch straight away)",
            _ => "",
        };

    /// <summary>
    /// Runs the editor until the user goes back. Returns the profile as it now is - renaming
    /// replaces it - or null if they pressed ^C, which leaves tabard altogether. The frame grows if
    /// the editor needs more room than the picker had; the caller's <paramref name="top"/> and
    /// <paramref name="frameHeight"/> follow it.
    /// </summary>
    public static Profile? Run(
        Profile profile,
        ref int top,
        ref int frameHeight,
        List<string> notes
    )
    {
        Grow(ref top, ref frameHeight, Height(profile));

        var fields = Fields(profile);
        var index = 0;
        StringBuilder? draft = null;
        string? status = null;

        while (true)
        {
            var field = fields[index];
            var width = Term.WindowWidth();
            var lines = new List<Picker.Line>(frameHeight)
            {
                new(
                    $"  Edit '{profile.Name}' - {Harnesses.DisplayName(profile.Harness)}",
                    ConsoleColor.White
                ),
                new("", null),
            };

            var caret = 0;
            for (var i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                var selected = i == index;
                var prefix = $"  {(selected ? ">" : " ")} {Label(f).PadRight(LabelWidth)}";

                if (selected && draft is not null)
                {
                    // The end of a long value is the part being typed, so that is what stays in view.
                    var room = Math.Max(1, width - prefix.Length - 1);
                    var text = draft.ToString();
                    var visible = text.Length > room ? text[^room..] : text;

                    caret = prefix.Length + visible.Length;
                    lines.Add(new(prefix + visible, ConsoleColor.Yellow));
                    continue;
                }

                var value = Value(f, profile);
                var shown = value.Length > 0 ? value : Placeholder(f);

                lines.Add(
                    new(
                        prefix + shown,
                        selected ? ConsoleColor.Cyan
                            : value.Length == 0 ? ConsoleColor.DarkGray
                            : null
                    )
                );
            }

            lines.Add(new("", null));
            lines.Add(new("  " + Hint(field, profile), ConsoleColor.DarkGray));
            lines.Add(new(Help(draft is not null, field is Field.Sandbox), ConsoleColor.DarkGray));
            lines.Add(new(status is null ? "" : "  " + status, ConsoleColor.Yellow));

            Picker.Paint(lines, top, frameHeight);

            if (draft is null)
                Term.HideCursor();
            else
                Picker.Caret(top + HeaderLines + index, caret);

            var key = Term.ReadKey();
            var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            status = null;

            if (key.Key == ConsoleKey.C && control)
                return null;

            if (draft is not null)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        try
                        {
                            (profile, status) = Save(field, profile, draft.ToString(), notes);
                            draft = null;
                        }
                        catch (Exception ex)
                        {
                            // Stay in the field: a rejected value is one to correct, not retype.
                            status = ex.Message;
                        }

                        break;

                    case ConsoleKey.Escape:
                        if (!Term.SwallowSplitEscape())
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

            switch (key.Key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.K:
                    index = (index - 1 + fields.Count) % fields.Count;
                    break;

                case ConsoleKey.DownArrow
                or ConsoleKey.J:
                    index = (index + 1) % fields.Count;
                    break;

                case ConsoleKey.Enter
                or ConsoleKey.Spacebar when field is Field.Sandbox:
                    try
                    {
                        var on = !Sandboxing.IsOn(profile.Name);
                        using (ProfileStore.AcquireLock())
                            Sandboxing.Set(profile.Name, on);

                        status = Sandboxing.Status(profile.Name, on);
                    }
                    catch (Exception ex)
                    {
                        status = ex.Message;
                    }

                    break;

                case ConsoleKey.Enter:
                    // Seeded with the current value, so a small correction is a small edit.
                    draft = new StringBuilder(Value(field, profile));
                    break;

                case ConsoleKey.Escape:
                    if (Term.SwallowSplitEscape())
                        break;

                    return profile;

                case ConsoleKey.Q
                or ConsoleKey.E
                or ConsoleKey.LeftArrow:
                    return profile;
            }
        }
    }

    private static (Profile Profile, string? Status) Save(
        Field field,
        Profile profile,
        string value,
        List<string> notes
    )
    {
        value = value.Trim();

        switch (field)
        {
            case Field.Name:
                var result = ProfileStore.Rename(profile, value);
                notes.AddRange(result.Warnings);

                return (
                    result.Profile,
                    string.Equals(result.Profile.Name, profile.Name, StringComparison.Ordinal)
                        ? null
                        : $"renamed '{profile.Name}' to '{result.Profile.Name}'"
                );

            case Field.Image:
                using (ProfileStore.AcquireLock())
                    Sbx.SetImage(profile.Name, value.Length == 0 ? null : value);

                return (
                    profile,
                    value.Length == 0
                        ? $"'{profile.Name}' will use sbx's default image."
                        : $"'{profile.Name}' will make new sandboxes from {value}."
                );

            case Field.Companion:
                using (ProfileStore.AcquireLock())
                    Companions.Set(profile.Name, value);

                return (
                    profile,
                    value.Length == 0
                        ? $"'{profile.Name}' will start nothing alongside."
                        : $"'{profile.Name}' will start '{value}' alongside."
                );

            case Field.WaitFor:
                using (ProfileStore.AcquireLock())
                    Companions.SetWaitFor(profile.Name, value);

                return (
                    profile,
                    value.Length == 0
                        ? $"'{profile.Name}' will launch straight away."
                        : $"'{profile.Name}' will launch once {value} answers."
                );

            default:
                return (profile, null);
        }
    }

    /// <summary>
    /// Adds rows to the bottom of the frame so it can hold <paramref name="needed"/> lines, by
    /// printing newlines from its last line - the terminal scrolls if it has to, and the frame's top
    /// moves up with it.
    /// </summary>
    private static void Grow(ref int top, ref int frameHeight, int needed)
    {
        if (needed <= frameHeight)
            return;

        try
        {
            Console.SetCursorPosition(0, top + frameHeight - 1);
            for (var i = frameHeight; i < needed; i++)
                Console.WriteLine();

            top = Math.Max(0, Console.CursorTop - needed + 1);
            frameHeight = needed;
        }
        catch
        {
            // Window shrank under us; the editor draws in what there is.
        }
    }
}
