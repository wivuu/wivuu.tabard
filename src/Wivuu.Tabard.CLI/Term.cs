namespace Wivuu.Tabard.Cli;

/// <summary>
/// The terminal handling the picker and the wizard both need. Everything here fails soft: a window
/// that has gone away, or was never a window, must not take the program down with it.
/// </summary>
internal static class Term
{
    /// <summary>False under a pipe or in CI, where drawing a frame and reading keys is meaningless.</summary>
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// Trims to a UTF-16 length without splitting a surrogate pair. Not display columns - a row of
    /// CJK still overflows - but it never emits half a character.
    /// </summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var end = max;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;

        return text[..end];
    }

    public static int WindowHeight()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The width to pad rows to, one short of the window so a full row cannot wrap.</summary>
    public static int WindowWidth()
    {
        try
        {
            return Math.Max(20, Console.WindowWidth - 1);
        }
        catch
        {
            return 80;
        }
    }

    public static void ShowCursor()
    {
        try
        {
            Console.CursorVisible = true;
            Console.ResetColor();
        }
        catch
        {
            // Nothing useful to do if the terminal has gone.
        }
    }

    public static void HideCursor()
    {
        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
            // As above.
        }
    }

    /// <summary>
    /// An arrow key that arrives split across two reads surfaces as a bare Escape with the rest of
    /// the sequence behind it, and quitting on that loses the user's keypress. Console.KeyAvailable
    /// is no help - it stays false because the remainder is already buffered inside the reader - so
    /// the only way to tell is a timed read: an introducer arriving within a few dozen milliseconds
    /// was not typed by a human. On a real Escape that read is still outstanding when we return,
    /// which is safe only because escaping abandons the command rather than reading another key.
    /// </summary>
    public static bool SwallowSplitEscape()
    {
        var probe = Task.Run(() => Console.ReadKey(intercept: true));
        if (!probe.Wait(TimeSpan.FromMilliseconds(50)) || probe.Result.KeyChar is not ('[' or 'O'))
            return false;

        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);

        return true;
    }
}
