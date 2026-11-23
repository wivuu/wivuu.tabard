using System.Text;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// The profile editor's Network screen: every site an srt sandbox of the profile may reach, where
/// each comes from, and the keys to change it. The agent's own sites are listed but fixed - it
/// cannot work without them. Sites added here can be removed again, and the developer defaults can
/// be blocked for this profile alone. Saved as each change is made, like the rest of the editor.
/// </summary>
internal static class NetworkEditor
{
    private const int ChromeLines = 6; // title, blank, blank, hint, help, status
    private const int HeaderLines = 2;
    private const int SiteWidth = 34;
    private const int MaxHeight = 24;

    internal const string Help =
        "  up/down move   space allow/block   a add   x remove   r reset   esc back";

    /// <summary>
    /// Runs the screen until the user goes back. Returns false if they pressed ^C, which leaves
    /// tabard altogether. Grows the frame to show more of the list if the window has room.
    /// </summary>
    public static bool Run(Profile profile, ref int top, ref int frameHeight)
    {
        var hostPorts = Sandboxes.HostPorts(profile);
        var entries = SrtSandbox.Network(profile, hostPorts);

        var wanted = Math.Min(
            entries.Count + ChromeLines,
            Math.Min(MaxHeight, Math.Max(frameHeight, Term.WindowHeight() - 2))
        );
        ProfileEditor.Grow(ref top, ref frameHeight, wanted);

        var index = 0;
        var offset = 0;
        StringBuilder? draft = null;
        string? status = null;
        var unicode = Term.Unicode;
        var agent = Harnesses.DisplayName(profile.Harness);

        while (true)
        {
            entries = SrtSandbox.Network(profile, hostPorts);
            index = Math.Clamp(index, 0, entries.Count - 1);

            var rows = Math.Max(1, frameHeight - ChromeLines);
            if (index < offset)
                offset = index;
            else if (index >= offset + rows)
                offset = index - rows + 1;
            offset = Math.Clamp(offset, 0, Math.Max(0, entries.Count - rows));

            var allowed = entries.Count(e => e.Allowed);
            var more =
                entries.Count > rows
                    ? $"   {offset + 1}-{Math.Min(entries.Count, offset + rows)} of {entries.Count}"
                    : "";
            var lines = new List<Picker.Line>(frameHeight)
            {
                new(
                    $"  Network for '{profile.Name}' - {allowed} sites allowed{more}",
                    ConsoleColor.White
                ),
                new("", null),
            };

            for (var i = offset; i < Math.Min(entries.Count, offset + rows); i++)
            {
                var entry = entries[i];
                var selected = i == index;
                var mark = entry.Allowed ? (unicode ? "✓" : "+") : (unicode ? "✗" : "-");
                var source = entry.Source switch
                {
                    SrtSandbox.Source.Agent => $"needed by {agent}",
                    SrtSandbox.Source.Added => "added",
                    _ => entry.Allowed ? "default" : "default, blocked",
                };
                var text =
                    $"  {(selected ? ">" : " ")} {mark} {entry.Site.PadRight(SiteWidth)} {source}";

                lines.Add(
                    new(
                        text,
                        selected ? ConsoleColor.Cyan
                            : entry.Allowed ? null
                            : ConsoleColor.DarkGray,
                        new Picker.Accent(
                            4,
                            mark.Length,
                            entry.Allowed ? ConsoleColor.Green : ConsoleColor.Red
                        )
                    )
                );
            }

            while (lines.Count < HeaderLines + rows)
                lines.Add(new("", null));

            var caret = 0;
            lines.Add(new("", null));
            if (draft is not null)
            {
                const string prompt = "  Add: ";
                var room = Math.Max(1, Term.WindowWidth() - prompt.Length - 1);
                var text = draft.ToString();
                var visible = text.Length > room ? text[^room..] : text;
                caret = prompt.Length + visible.Length;
                lines.Add(new(prompt + visible, ConsoleColor.Yellow));
            }
            else
            {
                lines.Add(new("  " + Hint(entries[index], agent), ConsoleColor.DarkGray));
            }

            lines.Add(
                new(draft is null ? Help : ProfileEditor.Help(true, false), ConsoleColor.DarkGray)
            );
            lines.Add(new(status is null ? "" : "  " + status, ConsoleColor.Yellow));

            Picker.Paint(lines, top, frameHeight);

            if (draft is null)
                Term.HideCursor();
            else
                Picker.Caret(top + HeaderLines + rows + 1, caret);

            var key = Term.ReadKey();
            var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            status = null;

            if (key.Key == ConsoleKey.C && control)
                return false;

            if (draft is not null)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        try
                        {
                            (status, var added) = Add(profile, draft.ToString(), entries);
                            draft = null;

                            if (added is not null)
                            {
                                var at = SrtSandbox
                                    .Network(profile, hostPorts)
                                    .FindIndex(e =>
                                        string.Equals(
                                            e.Site,
                                            added,
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                    );
                                if (at >= 0)
                                    index = at;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Stay in the field: a rejected site is one to correct, not retype.
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

            var current = entries[index];

            try
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        index = (index - 1 + entries.Count) % entries.Count;
                        break;

                    case ConsoleKey.DownArrow
                    or ConsoleKey.J:
                        index = (index + 1) % entries.Count;
                        break;

                    case ConsoleKey.PageUp:
                        index = Math.Max(0, index - rows);
                        break;

                    case ConsoleKey.PageDown:
                        index = Math.Min(entries.Count - 1, index + rows);
                        break;

                    case ConsoleKey.Home:
                        index = 0;
                        break;

                    case ConsoleKey.End:
                        index = entries.Count - 1;
                        break;

                    case ConsoleKey.Spacebar
                    or ConsoleKey.Enter:
                        status = Toggle(profile, current, agent);
                        break;

                    case ConsoleKey.X
                    or ConsoleKey.Delete
                    or ConsoleKey.Backspace:
                        status = current is { Source: SrtSandbox.Source.Developer, Allowed: false }
                            ? $"{current.Site} is already blocked."
                            : Toggle(profile, current, agent);
                        break;

                    case ConsoleKey.A
                    or ConsoleKey.Insert:
                        draft = new StringBuilder();
                        break;

                    case ConsoleKey.R:
                        if (SandboxOptions.BlockedOf(profile.Name).Count == 0)
                        {
                            status = "every default site is already allowed.";
                            break;
                        }

                        using (ProfileStore.AcquireLock())
                            SandboxOptions.ClearBlocked(profile.Name);
                        status = "every default site is allowed again.";
                        break;

                    case ConsoleKey.Escape:
                        if (Term.SwallowSplitEscape())
                            break;
                        return true;

                    case ConsoleKey.Q
                    or ConsoleKey.LeftArrow:
                        return true;
                }
            }
            catch (Exception ex)
            {
                status = ex.Message;
            }
        }
    }

    internal static string Hint(SrtSandbox.NetworkEntry entry, string agent) =>
        entry.Source switch
        {
            SrtSandbox.Source.Agent => $"{agent} needs this to work, so it is always allowed.",
            SrtSandbox.Source.Added => "Added for this profile. x removes it.",
            _ when entry.Allowed => "Open to every srt sandbox. Space blocks it for this profile.",
            _ => "Blocked for this profile. Space allows it again.",
        };

    /// <summary>Space on a row: block or allow a default, remove an added site.</summary>
    private static string Toggle(Profile profile, SrtSandbox.NetworkEntry entry, string agent)
    {
        switch (entry.Source)
        {
            case SrtSandbox.Source.Agent:
                return $"{agent} needs {entry.Site}, so it cannot be blocked.";

            case SrtSandbox.Source.Added:
                var rest = SandboxOptions
                    .AllowedOf(profile.Name)
                    .Where(s => !string.Equals(s, entry.Site, StringComparison.OrdinalIgnoreCase));
                using (ProfileStore.AcquireLock())
                    SandboxOptions.SetAllowed(profile.Name, string.Join(' ', rest));
                return $"removed {entry.Site}.";

            default:
                using (ProfileStore.AcquireLock())
                    SandboxOptions.SetBlocked(profile.Name, entry.Site, entry.Allowed);
                return entry.Allowed
                    ? $"blocked {entry.Site} for '{profile.Name}'."
                    : $"allowed {entry.Site} again.";
        }
    }

    /// <summary>
    /// Adds what was typed - one site or several. A blocked default is unblocked rather than added
    /// twice, and a site already allowed is left alone. Returns the status line and the last site
    /// it changed, to move to. Throws if something typed is not a site.
    /// </summary>
    private static (string Status, string? Site) Add(
        Profile profile,
        string text,
        IReadOnlyList<SrtSandbox.NetworkEntry> entries
    )
    {
        var sites = SandboxOptions.ParseSites(text);
        if (sites.Count == 0)
            return ("nothing added.", null);

        var added = new List<string>();
        var unblocked = new List<string>();
        var already = new List<string>();

        using (ProfileStore.AcquireLock())
        {
            var allow = SandboxOptions.AllowedOf(profile.Name).ToList();

            foreach (var site in sites)
            {
                var existing = entries.FirstOrDefault(e =>
                    string.Equals(e.Site, site, StringComparison.OrdinalIgnoreCase)
                );

                if (existing is { Source: SrtSandbox.Source.Developer, Allowed: false })
                {
                    SandboxOptions.SetBlocked(profile.Name, existing.Site, false);
                    unblocked.Add(existing.Site);
                }
                else if (existing is not null)
                {
                    already.Add(existing.Site);
                }
                else
                {
                    allow.Add(site);
                    added.Add(site);
                }
            }

            if (added.Count > 0)
                SandboxOptions.SetAllowed(profile.Name, string.Join(' ', allow));
        }

        var parts = new List<string>();
        if (added.Count > 0)
            parts.Add($"added {string.Join(", ", added)}");
        if (unblocked.Count > 0)
            parts.Add($"allowed {string.Join(", ", unblocked)} again");
        if (already.Count > 0)
            parts.Add($"{string.Join(", ", already)} already allowed");

        return (
            string.Join("; ", parts) + ".",
            added.LastOrDefault() ?? unblocked.LastOrDefault() ?? already.LastOrDefault()
        );
    }
}
