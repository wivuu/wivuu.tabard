namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// Only the two pure helpers. The rest of the picker is a render loop against a real console, so
/// these are the parts that can be wrong without anyone noticing until they are on screen.
/// </summary>
public class PickerTests
{
    [Test]
    [Arguments(0, "1 ")]
    [Arguments(8, "9 ")]
    public async Task Gutter_numbers_the_first_nine_rows_from_one(int index, string expected)
    {
        await Assert.That(Picker.Gutter(index)).IsEqualTo(expected);
    }

    /// <summary>Past the ninth there is no key to print, and the blank has to be the same width
    /// as a number or the names below it stop lining up.</summary>
    [Test]
    public async Task Gutter_is_blank_past_the_ninth_row()
    {
        await Assert.That(Picker.Gutter(9)).IsEqualTo("  ");
        await Assert.That(Picker.Gutter(50)).IsEqualTo("  ");
    }

    [Test]
    public async Task MoveTo_carries_the_item_to_its_new_place()
    {
        var items = new List<string> { "a", "b", "c" };

        await Assert.That(Picker.MoveTo(items, 2, 1)).IsEqualTo(1);
        await Assert.That(string.Join(",", items)).IsEqualTo("a,c,b");
    }

    [Test]
    public async Task MoveTo_reaches_across_the_list()
    {
        var items = new List<string> { "a", "b", "c", "d" };

        await Assert.That(Picker.MoveTo(items, 0, 3)).IsEqualTo(3);
        await Assert.That(string.Join(",", items)).IsEqualTo("b,c,d,a");
    }

    /// <summary>
    /// Clamped, not wrapped. The cursor wraps because that costs nothing, but rotating the list
    /// by one renumbers every row - which is the one thing the numbers exist not to do.
    /// </summary>
    [Test]
    [Arguments(0, -1)]
    [Arguments(2, 3)]
    public async Task MoveTo_stops_at_the_ends(int from, int to)
    {
        var items = new List<string> { "a", "b", "c" };

        await Assert.That(Picker.MoveTo(items, from, to)).IsEqualTo(from);
        await Assert.That(string.Join(",", items)).IsEqualTo("a,b,c");
    }

    [Test]
    public async Task MoveTo_leaves_a_single_item_alone()
    {
        var items = new List<string> { "only" };

        await Assert.That(Picker.MoveTo(items, 0, -1)).IsEqualTo(0);
        await Assert.That(string.Join(",", items)).IsEqualTo("only");
    }
}
