using System.Net;
using Microsoft.Azure.Cosmos;
using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Infrastructure.Leases;

public interface IProcessorLeaseStore
{
    Task<ProcessorLeaseDocument?> TryAcquireAsync(string processor, string region, string ownerId, DateTimeOffset now, CancellationToken cancel);
    Task<ProcessorLeaseDocument> RenewAsync(ProcessorLeaseDocument lease, DateTimeOffset expiresAt, CancellationToken cancel);
    Task<ProcessorLeaseDocument> SaveCheckpointAsync(ProcessorLeaseDocument lease, ChangeFeedProgressDocument checkpoint, CancellationToken cancel);
    Task ReleaseAsync(ProcessorLeaseDocument lease, CancellationToken cancel);
}

public sealed class ProcessorLeaseStore : IProcessorLeaseStore
{
    private readonly Container _container;
    private readonly ILogger<ProcessorLeaseStore> _logger;

    public ProcessorLeaseStore(AccountDatabase database, ILogger<ProcessorLeaseStore> logger)
    {
        _container = database.GetContainer("accounts");
        _logger = logger;
    }

    public async Task<ProcessorLeaseDocument?> TryAcquireAsync(string processor, string region, string ownerId, DateTimeOffset now, CancellationToken cancel)
    {
        var key = ProcessorLeaseDocument.Key(processor, region);
        ProcessorLeaseDocument? current = null;
        try
        {
            current = (await _container.ReadItemAsync<ProcessorLeaseDocument>(key.Id, new PartitionKey(key.Pk), cancellationToken: cancel)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
        }

        if (current != null && current.ExpiresAt > now)
            return null;

        var lease = new ProcessorLeaseDocument(processor, region, ownerId, Guid.NewGuid().ToString("N"), now + ProcessorLease.Duration);
        try
        {
            if (current == null)
                return (await _container.CreateItemAsync(lease, new PartitionKey(key.Pk), new ItemRequestOptions { EnableContentResponseOnWrite = true }, cancel)).Resource;

            var acquired = (await _container.ReplaceItemAsync(lease, key.Id, new PartitionKey(key.Pk),
                new ItemRequestOptions { IfMatchEtag = GetETag(current), EnableContentResponseOnWrite = true }, cancel)).Resource;
            _logger.LogInformation("Took over expired {Processor} lease for {Region} from {PreviousOwner} with acquisition {Acquisition}.",
                processor, region, current.OwnerId, acquired.AcquisitionId);
            return acquired;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ProcessorLeaseDocument> RenewAsync(ProcessorLeaseDocument lease, DateTimeOffset expiresAt, CancellationToken cancel)
        => (await _container.ReplaceItemAsync(lease with { ExpiresAt = expiresAt }, lease.Id, new PartitionKey(lease.Pk),
            new ItemRequestOptions { IfMatchEtag = GetETag(lease), EnableContentResponseOnWrite = true }, cancel)).Resource;

    public async Task<ProcessorLeaseDocument> SaveCheckpointAsync(ProcessorLeaseDocument lease, ChangeFeedProgressDocument checkpoint, CancellationToken cancel)
    {
        if (checkpoint.Pk != lease.Pk)
            throw new InvalidOperationException("The checkpoint must belong to the leased processor and region.");

        using var response = await _container.CreateTransactionalBatch(new PartitionKey(lease.Pk))
            .ReplaceItem(lease.Id, lease, new TransactionalBatchItemRequestOptions { IfMatchEtag = GetETag(lease), EnableContentResponseOnWrite = true })
            .UpsertItem(checkpoint)
            .ExecuteAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lease-protected checkpoint failed: {response.StatusCode}.");
        return response.GetOperationResultAtIndex<ProcessorLeaseDocument>(0).Resource;
    }

    private static string GetETag(ProcessorLeaseDocument lease)
        => lease.ETag ?? throw new InvalidOperationException("A stored lease must have an ETag.");

    public async Task ReleaseAsync(ProcessorLeaseDocument lease, CancellationToken cancel)
    {
        try
        {
            await _container.DeleteItemAsync<ProcessorLeaseDocument>(lease.Id, new PartitionKey(lease.Pk),
                new ItemRequestOptions { IfMatchEtag = GetETag(lease) }, cancel);
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            // Another acquisition owns the lease now, or it has already been released.
        }
    }
}
