using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>Cards are dealt into the column stacks, never laid out as a row below them.</summary>
public class ColumnDealTests
{
    [Fact]
    public void TheDesktopTierDealsTheSecondInsightColumnIntoTheShortestStacks()
    {
        // Status holds four widgets, the first insight column five, activity three.
        Assert.Equal([2, 0, 2, 0, 1], ColumnDeal.Assign(5, 4, 5, 3));
    }

    [Fact]
    public void TheMediumTierDealsEveryCardIntoTheTwoStacks()
    {
        var targets = ColumnDeal.Assign(11, 4, 3);
        Assert.Equal(1, targets[0]);
        // Both stacks end within one widget of each other.
        var status = 4 + targets.Count(t => t == 0);
        var activity = 3 + targets.Count(t => t == 1);
        Assert.InRange(Math.Abs(status - activity), 0, 1);
    }

    [Fact]
    public void ATieGoesToTheLeftmostStack() =>
        Assert.Equal([0, 1, 0], ColumnDeal.Assign(3, 2, 2));
}
