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
    private readonly ILogger<ContentConflictResolutionService> _logger;

    public ContentConflictMerger(ContentContainer container, ILogger<ContentConflictResolutionService> logger)
    {
        _container = container;
        _logger = logger;
    }

    public async Task<bool> MergeAsync(Document? remoteConflict, Document? localConflict, OperationContext context)
    {
        if (remoteConflict is null)
        {
            _logger.LogWarning("Remote conflict is null.");
            return true;
        }

        if (localConflict is null)
        {
            _logger.LogWarning("Local conflict is null.");
            return true;
        }

        if (remoteConflict.GetType() != localConflict.GetType())
        {
            _logger.LogWarning("Different document types Remote:{remote} Local:{local}.", remoteConflict.GetType().Name, localConflict.GetType().Name);
            return false;
        }
        
        switch (remoteConflict)
        {
            case ConversationDocument remoteConversation:
                if(TryMergeConversation(remoteConversation, (ConversationDocument)localConflict, out var merged))
                {
                    _logger.LogInformation("Merging conversation {pk}/{id}", merged!.Pk, merged!.Id);
                    var uow = _container.CreateUnitOfWork(merged.Pk);
                    uow.Update(merged!);
                    await uow.SaveChangesAsync(context);
                    return true;
                }

                break;
                    
            case ConversationCountsDocument remoteCounts:
                if(await TryMergeConversationCountsAsync(remoteCounts, (ConversationCountsDocument)localConflict, context))
                    return true;

                break;
        }

        _logger.LogWarning("Unable to merge {type} {pk}/{id}.", remoteConflict!.GetType().Name,  remoteConflict!.Pk, remoteConflict!.Id);
        return false;
    }

    private async Task<bool> TryMergeConversationCountsAsync(ConversationCountsDocument remoteConflict, ConversationCountsDocument localConflict, OperationContext context)
    {
        var conversationCountKey = ConversationCountsDocument.Key(localConflict.UserId, localConflict.ConversationId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await _container.GetAsync<ConversationCountsDocument>(conversationCountKey, context);
            if(current == null)
                return false;

            var merged = current.MergeCounts(localConflict).MergeCounts(remoteConflict);
            var uow = _container.CreateUnitOfWork(merged.Pk);
            uow.Update(merged);
            try
            {
                await uow.SaveChangesAsync(context);
                _logger.LogInformation("Merging conversation counts {pk}/{id}.", merged.Pk, merged.Id);
                return true;
            }
            catch (UnitOfWorkException e) when (e.Error == OperationError.PreconditionFailed)
            {
            }
        }
    }

    private bool TryMergeConversation(ConversationDocument remoteConflict, ConversationDocument localConflict, out ConversationDocument? merged)
    {
        merged = remoteConflict.LastModify > localConflict.LastModify 
            ? remoteConflict with { ProcessingRegion = localConflict.ProcessingRegion }
            : localConflict;

        return true;
    }
}
