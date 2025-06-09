namespace SocialApp.WebApi.Infrastructure.Leases;

public sealed class ProcessorLeaseCoordinator
{
    private readonly IProcessorLeaseStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProcessorLeaseCoordinator> _logger;
    private readonly string _ownerId = Guid.NewGuid().ToString("N");

    public ProcessorLeaseCoordinator(IProcessorLeaseStore store, TimeProvider clock, ILogger<ProcessorLeaseCoordinator> logger)
    {
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(string processor, string region, Func<ProcessorLease, Task> process, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var document = await _store.TryAcquireAsync(processor, region, _ownerId, _clock.GetUtcNow(), cancel);
            if (document == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.NextDouble()), _clock, cancel);
                continue;
            }

            _logger.LogInformation("Acquired {Processor} lease for {Region}, acquisition {Acquisition}.",
                processor, region, document.AcquisitionId);
            using var lease = new ProcessorLease(document, _store, _clock, cancel);
            var renewal = RenewAsync(lease, processor, region);
            try
            {
                lease.Cancellation.ThrowIfCancellationRequested();
                await process(lease);
                if (!lease.Cancellation.IsCancellationRequested)
                    return;
            }
            catch (OperationCanceledException) when (lease.Cancellation.IsCancellationRequested)
            {
                cancel.ThrowIfCancellationRequested();
            }
            finally
            {
                if (lease.Cancellation.IsCancellationRequested && !cancel.IsCancellationRequested)
                    _logger.LogWarning("Stopped {Processor} for {Region} after lease loss or expiry.", processor, region);
                // The processing task has stopped before we release or try another acquisition.
                lease.Stop();
                await renewal;
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
                try
                {
                    await lease.ReleaseAsync(releaseTimeout.Token);
                    _logger.LogInformation("Released {Processor} lease for {Region} if still owned.", processor, region);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Could not release {Processor} lease for {Region}; it will expire.", processor, region);
                }
            }
        }
    }

    private async Task RenewAsync(ProcessorLease lease, string processor, string region)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _clock, lease.Cancellation);
                await lease.RenewAsync();
            }
        }
        catch (OperationCanceledException) when (lease.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Lost {Processor} lease for {Region}; stopping processing.", processor, region);
            lease.Stop();
        }
    }
}
