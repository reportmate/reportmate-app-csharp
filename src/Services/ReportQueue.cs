// Explicit usings: this file is also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReportMate.App.Services;

/// <summary>How one dashboard read ended.</summary>
/// <param name="Error">Null when it succeeded; otherwise the words the card shows.</param>
/// <param name="Transient">Worth one more try: a 5xx or a dropped connection, not a timeout.</param>
public sealed record FetchOutcome(string? Error, bool Transient = false, bool TimedOut = false)
{
    public static readonly FetchOutcome Success = new((string?)null);
    public bool Ok => Error is null;
}

/// <summary>
/// Runs the dashboard's reads a few at a time, each with a deadline, so a report
/// the API never answers turns into an error the card can offer to retry instead
/// of a spinner that never stops, and never holds a slot the other reads need.
/// </summary>
public static class ReportQueue
{
    /// <summary>How long one report may take before its card says it timed out.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// One read under its own deadline. A timeout is reported as an outcome, never
    /// thrown; the caller's own cancellation still propagates.
    /// </summary>
    public static async Task<FetchOutcome> RunAsync(
        Func<CancellationToken, Task<FetchOutcome>> fetch, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var work = Guard(fetch, deadline.Token);
        // Wait on the deadline as well as the work: a read that ignores its token
        // still releases its slot on time, and is abandoned rather than awaited.
        var expired = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        var first = await Task.WhenAny(work, expired);
        if (first == work)
        {
            var outcome = await work;
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            return outcome.TimedOut ? TimedOut(timeout) : outcome;
        }
        ct.ThrowIfCancellationRequested();
        return TimedOut(timeout);
    }

    private static FetchOutcome TimedOut(TimeSpan timeout) =>
        new($"The API did not answer within {timeout.TotalSeconds:0} seconds.", TimedOut: true);

    private static async Task<FetchOutcome> Guard(Func<CancellationToken, Task<FetchOutcome>> fetch, CancellationToken token)
    {
        try { return await fetch(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new FetchOutcome("Timed out.", TimedOut: true);
        }
        catch (Exception ex)
        {
            return new FetchOutcome(ex.Message);
        }
    }

    /// <summary>
    /// Every source, <paramref name="concurrency"/> at a time and in order, each under
    /// <paramref name="timeout"/> and retried once after <paramref name="retryDelay"/>
    /// when the failure was transient. <paramref name="done"/> runs as each finishes,
    /// on the caller's context.
    /// </summary>
    public static async Task RunAllAsync(
        IEnumerable<string> sources, int concurrency,
        Func<string, CancellationToken, Task<FetchOutcome>> fetch,
        Action<string, FetchOutcome> done,
        Func<string, TimeSpan>? timeout = null,
        TimeSpan? retryDelay = null)
    {
        var queue = new Queue<string>(sources);
        var running = new List<Task<(string, FetchOutcome)>>();

        async Task<(string, FetchOutcome)> One(string source)
        {
            var limit = timeout?.Invoke(source) ?? DefaultTimeout;
            var outcome = await RunAsync(t => fetch(source, t), limit);
            if (!outcome.Ok && outcome.Transient)
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(3));
                outcome = await RunAsync(t => fetch(source, t), limit);
            }
            return (source, outcome);
        }

        while (running.Count < concurrency && queue.Count > 0) running.Add(One(queue.Dequeue()));
        while (running.Count > 0)
        {
            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            if (queue.Count > 0) running.Add(One(queue.Dequeue()));
            var (source, outcome) = await finished;
            done(source, outcome);
        }
    }
}
