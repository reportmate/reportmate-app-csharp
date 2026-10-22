using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The dashboard's read queue: a report the API never answers becomes an error with
/// a retry, on time, and never holds up the reads behind it.
/// </summary>
public class ReportQueueTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task AReadThatHonoursItsTokenTimesOut()
    {
        var outcome = await ReportQueue.RunAsync(async t => { await Task.Delay(Timeout.Infinite, t); return FetchOutcome.Success; }, Short);
        Assert.False(outcome.Ok);
        Assert.True(outcome.TimedOut);
        Assert.Contains("did not answer", outcome.Error);
    }

    [Fact]
    public async Task AReadThatIgnoresItsTokenStillTimesOut()
    {
        var never = new TaskCompletionSource<FetchOutcome>();
        var outcome = await ReportQueue.RunAsync(_ => never.Task, Short);
        Assert.True(outcome.TimedOut);
    }

    [Fact]
    public async Task AFailedReadIsAnOutcomeNotAnException()
    {
        var outcome = await ReportQueue.RunAsync(_ => throw new InvalidOperationException("boom"), Short);
        Assert.Equal("boom", outcome.Error);
    }

    [Fact]
    public async Task TheCallersOwnCancellationPropagates()
    {
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReportQueue.RunAsync(async t => { await Task.Delay(Timeout.Infinite, t); return FetchOutcome.Success; },
                TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task OneHungReportDoesNotBlockTheOthers()
    {
        var never = new TaskCompletionSource<FetchOutcome>();
        var finished = new List<(string Source, FetchOutcome Outcome)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await ReportQueue.RunAllAsync(["hung", "a", "b", "c", "d"], 2,
            (source, _) => source == "hung" ? never.Task : Task.FromResult(FetchOutcome.Success),
            (source, outcome) => finished.Add((source, outcome)),
            _ => Short);

        Assert.Equal(5, finished.Count);
        // The others finished while the hung one held its slot, and it ended on its deadline.
        Assert.Equal(["a", "b", "c", "d", "hung"], finished.Select(f => f.Source));
        Assert.True(finished[^1].Outcome.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ATransientFailureIsRetriedOnceButATimeoutIsNot()
    {
        var calls = new Dictionary<string, int>();
        var finished = new Dictionary<string, FetchOutcome>();
        await ReportQueue.RunAllAsync(["flaky", "slow"], 2,
            async (source, t) =>
            {
                calls[source] = calls.GetValueOrDefault(source) + 1;
                if (source == "slow") { await Task.Delay(Timeout.Infinite, t); return FetchOutcome.Success; }
                return calls[source] == 1 ? new FetchOutcome("502", Transient: true) : FetchOutcome.Success;
            },
            (source, outcome) => finished[source] = outcome,
            _ => Short, retryDelay: TimeSpan.Zero);

        Assert.Equal(2, calls["flaky"]);
        Assert.True(finished["flaky"].Ok);
        Assert.Equal(1, calls["slow"]);
        Assert.True(finished["slow"].TimedOut);
    }
}
