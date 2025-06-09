using Microsoft.Extensions.Logging.Abstractions;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Infrastructure.Leases;

namespace SocialApp.Tests.LeaseTests;

public class ProcessorLeaseTests
{
    [Test]
    public async Task Replicas_Compete_WhileRegionsAndProcessorsRemainIndependent()
    {
        var clock = new ManualClock();
        var store = new MemoryLeaseStore();
        using var firstStop = new CancellationTokenSource();
        using var otherStop = new CancellationTokenSource();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var first = Coordinator(store, clock).RunAsync("content", "a", lease => WaitAsync(lease, firstStarted), firstStop.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Coordinator(store, clock).RunAsync("content", "a", lease => WaitAsync(lease, secondStarted), otherStop.Token);
        var otherRegion = Signal();
        var otherProcessor = Signal();
        var third = Coordinator(store, clock).RunAsync("content", "b", lease => WaitAsync(lease, otherRegion), otherStop.Token);
        var fourth = Coordinator(store, clock).RunAsync("conflicts", "a", lease => WaitAsync(lease, otherProcessor), otherStop.Token);
        try
        {
            await Task.WhenAll(otherRegion.Task, otherProcessor.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(secondStarted.Task.IsCompleted, Is.False);
            firstStop.Cancel();
            await StoppedAsync(first);
            clock.Advance(TimeSpan.FromSeconds(6));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            firstStop.Cancel();
            otherStop.Cancel();
            await Task.WhenAll(new[] { first, second, third, fourth }.Select(StoppedAsync));
        }
    }

    [Test]
    public async Task RenewalFailure_CancelsWork_AndWaitsForItBeforeRelease()
    {
        var clock = new ManualClock();
        var store = new MemoryLeaseStore { FailRenewal = true };
        using var stop = new CancellationTokenSource();
        var started = Signal();
        var cancelled = Signal();
        var finish = Signal();
        var running = Coordinator(store, clock).RunAsync("content", "a", async lease =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, lease.Cancellation);
            }
            finally
            {
                cancelled.TrySetResult();
                await finish.Task;
            }
        }, stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(15));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(store.ReleaseCount, Is.Zero);
        }
        finally
        {
            stop.Cancel();
            finish.TrySetResult();
            await StoppedAsync(running);
        }
        Assert.That(store.ReleaseCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Renewal_ExtendsDeadline_AndExpiryRejectsFurtherWork()
    {
        var clock = new ManualClock();
        var store = new MemoryLeaseStore();
        var document = (await store.TryAcquireAsync("content", "a", "owner", clock.GetUtcNow(), default))!;
        using var lease = new ProcessorLease(document, store, clock, default);
        clock.Advance(TimeSpan.FromSeconds(15));
        await lease.RenewAsync();
        clock.Advance(TimeSpan.FromSeconds(35));
        Assert.That(lease.Cancellation.IsCancellationRequested, Is.False);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.That(lease.Cancellation.IsCancellationRequested, Is.True);
        Assert.CatchAsync<OperationCanceledException>(async () => await lease.SaveCheckpointAsync(new("content", "a", "range", "token")));
        Assert.That(store.Checkpoints, Is.Empty);
    }

    [Test]
    public async Task CheckpointAndRenewal_AreSerialized_AndFailedCheckpointStopsLease()
    {
        var clock = new ManualClock();
        var store = new MemoryLeaseStore { PauseCheckpoint = Signal() };
        var document = (await store.TryAcquireAsync("content", "a", "owner", clock.GetUtcNow(), default))!;
        using var lease = new ProcessorLease(document, store, clock, default);
        var checkpoint = lease.SaveCheckpointAsync(new("content", "a", "range", "token"));
        await store.CheckpointStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var renewal = lease.RenewAsync();
        Assert.That(store.RenewalCount, Is.Zero);
        store.PauseCheckpoint.TrySetResult();
        await Task.WhenAll(checkpoint, renewal);
        Assert.That(store.RenewalCount, Is.EqualTo(1));
        store.FailCheckpoint = true;
        Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.SaveCheckpointAsync(new("content", "a", "range", "next")));
        Assert.That(lease.Cancellation.IsCancellationRequested, Is.True);
    }

    private static ProcessorLeaseCoordinator Coordinator(IProcessorLeaseStore store, TimeProvider clock)
        => new(store, clock, NullLogger<ProcessorLeaseCoordinator>.Instance);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitAsync(ProcessorLease lease, TaskCompletionSource started)
    {
        started.TrySetResult();
        return Task.Delay(Timeout.Infinite, lease.Cancellation);
    }

    private static async Task StoppedAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class MemoryLeaseStore : IProcessorLeaseStore
    {
        private readonly Dictionary<string, ProcessorLeaseDocument> _leases = new();
        public List<ChangeFeedProgressDocument> Checkpoints { get; } = new();
        public bool FailRenewal { get; init; }
        public bool FailCheckpoint { get; set; }
        public TaskCompletionSource? PauseCheckpoint { get; init; }
        public TaskCompletionSource CheckpointStarted { get; } = Signal();
        public int RenewalCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<ProcessorLeaseDocument?> TryAcquireAsync(string processor, string region, string ownerId, DateTimeOffset now, CancellationToken cancel)
        {
            lock (_leases)
            {
                var key = ProcessorLeaseDocument.Key(processor, region).Pk;
                if (_leases.TryGetValue(key, out var current) && current.ExpiresAt > now)
                    return Task.FromResult<ProcessorLeaseDocument?>(null);
                var document = new ProcessorLeaseDocument(processor, region, ownerId, Guid.NewGuid().ToString(), now + ProcessorLease.Duration);
                _leases[key] = document;
                return Task.FromResult<ProcessorLeaseDocument?>(document);
            }
        }

        public Task<ProcessorLeaseDocument> RenewAsync(ProcessorLeaseDocument lease, DateTimeOffset expiresAt, CancellationToken cancel)
        {
            lock (_leases)
            {
                RenewalCount++;
                CheckOwner(lease);
                var renewed = _leases[lease.Pk] = lease with { ExpiresAt = expiresAt };
                if (FailRenewal)
                    throw new InvalidOperationException("Renewal was applied but its response was lost.");
                return Task.FromResult(renewed);
            }
        }

        public async Task<ProcessorLeaseDocument> SaveCheckpointAsync(ProcessorLeaseDocument lease, ChangeFeedProgressDocument checkpoint, CancellationToken cancel)
        {
            CheckpointStarted.TrySetResult();
            if (PauseCheckpoint != null)
                await PauseCheckpoint.Task.WaitAsync(cancel);
            lock (_leases)
            {
                if (FailCheckpoint)
                    throw new InvalidOperationException("Checkpoint failed.");
                CheckOwner(lease);
                Checkpoints.Add(checkpoint);
                return _leases[lease.Pk] = lease with { };
            }
        }

        public Task ReleaseAsync(ProcessorLeaseDocument lease, CancellationToken cancel)
        {
            lock (_leases)
            {
                ReleaseCount++;
                if (_leases.TryGetValue(lease.Pk, out var current) && ReferenceEquals(current, lease))
                    _leases.Remove(lease.Pk);
                return Task.CompletedTask;
            }
        }

        private void CheckOwner(ProcessorLeaseDocument lease)
        {
            if (!_leases.TryGetValue(lease.Pk, out var current) || !ReferenceEquals(current, lease))
                throw new InvalidOperationException("Stale lease version.");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        private readonly List<ManualTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() { lock (_timers) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan duration)
        {
            List<ManualTimer> ready;
            lock (_timers)
            {
                _now += duration;
                ready = _timers.Where(t => t.Due <= _now).ToList();
                foreach (var timer in ready)
                    timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in ready)
                timer.Fire();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._timers) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
