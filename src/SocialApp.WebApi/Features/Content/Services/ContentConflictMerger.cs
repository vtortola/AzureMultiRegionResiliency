using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Content.Containers;

namespace SocialApp.WebApi.Features.Content.Services;

public interface IConflictMerger
{
    Task<bool> MergeAsync(Document? remoteConflict, Document? localConflict, OperationContext context);
}

public class ContentConflictMerger : IConflictMerger
{
    private readonly ContentContainer _container;
    private readonly string _processingRegion;
    public ContentConflictMerger(ContentContainer container, ILogger<ContentConflictResolutionService> logger, string processingRegion)
    {
        _container = container;
        _processingRegion = processingRegion;
    }

    public async Task<bool> MergeAsync(Document? remoteConflict, Document? localConflict, OperationContext context)
    {
        if (remoteConflict is not IRegionOwnedDocument remoteOwner || localConflict is not IRegionOwnedDocument localOwner
            || remoteConflict.GetType() != localConflict.GetType()
            || !string.Equals(remoteOwner.ProcessingRegion, _processingRegion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(localOwner.ProcessingRegion, _processingRegion, StringComparison.OrdinalIgnoreCase)) return false;

        var key = new DocumentKey(localConflict.Pk, localConflict.Id);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await _container.GetAsync<Document>(key, context);
            if (current is not IRegionOwnedDocument owner || current.GetType() != remoteConflict.GetType()
                || !string.Equals(owner.ProcessingRegion, _processingRegion, StringComparison.OrdinalIgnoreCase)) return false;
            var winner = remoteOwner.CreationDate > owner.CreationDate ? remoteConflict : current;
            // Always write the winner before deleting the conflict so it is observable in the change feed.
            var uow = _container.CreateUnitOfWork(key.Pk);
            uow.Update((dynamic)(winner with { ETag = current.ETag }));
            try { await uow.SaveChangesAsync(context); return true; }
            catch (UnitOfWorkException ex) when (ContentContainer.IsConcurrencyFailure(ex)) { }
        }
    }
}
