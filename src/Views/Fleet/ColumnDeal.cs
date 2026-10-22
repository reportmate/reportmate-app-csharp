namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Which stack each card goes to when the dashboard has fewer columns than the wide
/// layout: in order, each to the stack holding the fewest widgets so far, the leftmost
/// on a tie. Kept apart from the layout so the rule can be tested.
/// </summary>
public static class ColumnDeal
{
    /// <param name="cards">How many cards to deal.</param>
    /// <param name="counts">How many widgets each stack already holds.</param>
    /// <returns>The stack index for each card, in order.</returns>
    public static int[] Assign(int cards, params int[] counts)
    {
        var held = (int[])counts.Clone();
        var targets = new int[cards];
        for (var i = 0; i < cards; i++)
        {
            var target = Array.IndexOf(held, held.Min());
            targets[i] = target;
            held[target]++;
        }
        return targets;
    }
}
