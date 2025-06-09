using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Infrastructure.Leases;

public sealed class ProcessorLease : IDisposable
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(10);
    private readonly IProcessorLeaseStore _store;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop;
    private readonly ITimer _deadline;
    private readonly SemaphoreSlim _gate = new(1);
    private ProcessorLeaseDocument _document;

    public ProcessorLease(ProcessorLeaseDocument document, IProcessorLeaseStore store, TimeProvider clock, CancellationToken cancel)
    {
        _document = document;
        _store = store;
        _clock = clock;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _deadline = clock.CreateTimer(_ => StopFromTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ResetDeadline();
    }

    public CancellationToken Cancellation => _stop.Token;

    public void Stop() => _stop.Cancel();

    private void StopFromTimer()
    {
        try
        {
            Stop();
        }
        catch (ObjectDisposedException)
        {
            // A timer callback may already be queued when the completed lease is disposed.
        }
    }

    public async Task RenewAsync()
    {
        await _gate.WaitAsync(Cancellation);
        try
        {
            EnsureValid();
            _document = await _store.RenewAsync(_document, _clock.GetUtcNow() + Duration, Cancellation);
            ResetDeadline();
        }
        catch
        {
            Stop();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCheckpointAsync(ChangeFeedProgressDocument checkpoint)
    {
        await _gate.WaitAsync(Cancellation);
        try
        {
            EnsureValid();
            _document = await _store.SaveCheckpointAsync(_document, checkpoint, Cancellation);
        }
        catch
        {
            Stop();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            await _store.ReleaseAsync(_document, cancel);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureValid()
    {
        if (_clock.GetUtcNow() >= _document.ExpiresAt - SafetyMargin)
            Stop();
        Cancellation.ThrowIfCancellationRequested();
    }

    private void ResetDeadline()
    {
        var remaining = _document.ExpiresAt - SafetyMargin - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            Stop();
            return;
        }
        _deadline.Change(remaining, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _deadline.Dispose();
        _stop.Dispose();
        _gate.Dispose();
    }
}
