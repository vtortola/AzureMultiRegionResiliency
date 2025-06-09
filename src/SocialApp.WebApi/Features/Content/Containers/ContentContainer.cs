using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;

namespace SocialApp.WebApi.Features.Content.Containers;

public sealed class ContentContainer : CosmoContainer
{
    public ContentContainer(UserDatabase database) : base(database, "contents") { }

    internal static DateTime NextCreationDate(DateTime observed, OperationContext context)
        => context.UtcNow.UtcDateTime > observed ? context.UtcNow.UtcDateTime : observed.AddTicks(1);

    public static bool IsConcurrencyFailure(UnitOfWorkException error)
        => error.Error is OperationError.Conflict or OperationError.PreconditionFailed or OperationError.NotFound;

    private ConversationCountsDocument Advance(ConversationCountsDocument counts, OperationContext context)
        => counts with { CreationDate = NextCreationDate(counts.CreationDate, context) };

    public async Task<ConversationCountsDocument> EnsureCountsAsync(ConversationDocument conversation, OperationContext context)
    {
        var key = ConversationCountsDocument.Key(conversation.UserId, conversation.ConversationId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await GetAsync<ConversationCountsDocument>(key, context);
            if (current != null && conversation.CreationDate <= current.SourceCreationDate) return current;
            var updated = Advance(current ?? conversation.CreateCounts(), context) with
            {
                Deleted = conversation.Deleted, SourceCreationDate = conversation.CreationDate
            };
            var uow = CreateUnitOfWork(key.Pk);
            if (current == null) uow.Create(updated); else uow.Update(updated);
            try { await uow.SaveChangesAsync(context); }
            catch (UnitOfWorkException ex) when (IsConcurrencyFailure(ex)) { continue; }
            // Read the committed ETag for any subsequent conditional counter mutation.
        }
    }

    public async Task SetConversationLikeAsync(ConversationUserLikeDocument source, OperationContext context)
    {
        var reactionKey = ConversationLikeDocument.Key(source.ConversationUserId, source.ConversationId, source.UserId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var conversation = await GetAsync<ConversationDocument>(ConversationDocument.Key(source.ConversationUserId, source.ConversationId), context)
                ?? throw new InvalidOperationException("Reaction conversation is not available yet.");
            if (conversation.Deleted) return;
            var counts = await EnsureCountsAsync(conversation, context);
            if (counts.Deleted) return;
            var current = await GetAsync<ConversationLikeDocument>(reactionKey, context);
            if (current != null && source.CreationDate <= current.CreationDate) return;
            var updated = (current ?? new ConversationLikeDocument(source.ConversationUserId, source.ConversationId, source.UserId, false)) with
            {
                Like = source.Like, CreationDate = source.CreationDate, ProcessingRegion = conversation.ProcessingRegion, Ttl = -1
            };
            var delta = (source.Like ? 1 : 0) - (current?.Like == true ? 1 : 0);
            var uow = CreateUnitOfWork(reactionKey.Pk);
            if (current == null) uow.Create(updated); else uow.Update(updated);
            // Also condition on the counter when delta is zero, to serialize with deletion.
            uow.Update(Advance(counts, context) with { LikeCount = checked(counts.LikeCount + delta) });
            try { await uow.SaveChangesAsync(context); return; }
            catch (UnitOfWorkException ex) when (IsConcurrencyFailure(ex)) { }
        }
    }

    // Request-side deletion persists only source intent. The processor owns all derived writes.
    public async Task<ConversationDocument?> MarkConversationDeletedAsync(string userId, string conversationId, OperationContext context)
    {
        var key = ConversationDocument.Key(userId, conversationId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await GetAsync<ConversationDocument>(key, context);
            if (current == null || current.Deleted) return current;
            var deleted = current with
            {
                Deleted = true, Ttl = -1, Version = current.Version + 1, LastModify = context.UtcNow.UtcDateTime,
                CreationDate = NextCreationDate(current.CreationDate, context)
            };
            var uow = CreateUnitOfWork(key.Pk);
            uow.Update(deleted);
            try { await uow.SaveChangesAsync(context); return deleted; }
            catch (UnitOfWorkException ex) when (IsConcurrencyFailure(ex)) { }
        }
    }

    public async Task SyncParentCommentAsync(ConversationDocument source, OperationContext context)
    {
        if (source.IsRootConversation) return;
        if (string.IsNullOrWhiteSpace(source.ParentConversationUserId) || string.IsNullOrWhiteSpace(source.ParentConversationId))
            throw new InvalidOperationException("A comment must identify its parent.");
        var key = CommentDocument.Key(source.ParentConversationUserId, source.ParentConversationId, source.ConversationId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var parent = await GetAsync<ConversationDocument>(ConversationDocument.Key(source.ParentConversationUserId, source.ParentConversationId), context)
                ?? throw new InvalidOperationException("Parent conversation is not available yet.");
            var parentCounts = await EnsureCountsAsync(parent, context);
            var current = await GetAsync<CommentDocument>(key, context);
            if (current != null && source.CreationDate <= current.CreationDate) return;
            var deleted = source.Deleted || parent.Deleted;
            var updated = (current ?? new CommentDocument(source.ParentConversationUserId, source.ParentConversationId,
                source.UserId, source.ConversationId, source.Content, source.LastModify, source.Version, source.ProcessingRegion)) with
            {
                Content = source.Content, LastModify = source.LastModify, Version = source.Version,
                CreationDate = source.CreationDate, Deleted = deleted, Ttl = -1
            };
            var delta = (deleted ? 0 : 1) - (current is { Deleted: false } ? 1 : 0);
            var uow = CreateUnitOfWork(key.Pk);
            if (current == null) uow.Create(updated); else uow.Update(updated);
            uow.Update(Advance(parentCounts, context) with { CommentCount = checked(parentCounts.CommentCount + delta) });
            try { await uow.SaveChangesAsync(context); return; }
            catch (UnitOfWorkException ex) when (IsConcurrencyFailure(ex)) { }
        }
    }

    public async Task ApplyViewAsync(ConversationViewDocument source, OperationContext context)
    {
        var key = new DocumentKey(source.Pk, source.Id);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await GetAsync<ConversationViewDocument>(key, context);
            if (current == null || current.Applied) return;
            var conversation = await GetAsync<ConversationDocument>(ConversationDocument.Key(source.UserId, source.ConversationId), context)
                ?? throw new InvalidOperationException("Viewed conversation is not available yet.");
            var counts = await EnsureCountsAsync(conversation, context);
            var uow = CreateUnitOfWork(key.Pk);
            uow.Update(current with { Applied = true, CreationDate = NextCreationDate(current.CreationDate, context) });
            uow.Update(Advance(counts, context) with { ViewCount = checked(counts.ViewCount + (counts.Deleted ? 0 : 1)) });
            try { await uow.SaveChangesAsync(context); return; }
            catch (UnitOfWorkException ex) when (IsConcurrencyFailure(ex)) { }
        }
    }

    public async Task MergeParentCommentCountsAsync(ConversationCountsDocument source, OperationContext context)
    {
        if (source.IsRootConversation) return;
        var incoming = CommentCountsDocument.TryGenerateParentCommentCounts(source);
        if (incoming != null) await SaveProjectionAsync(this, incoming, source.ProcessingRegion, context);
    }

    public static async Task SaveProjectionAsync<T>(CosmoContainer container, T incoming, string owner, OperationContext context) where T : Document, IRegionOwnedDocument
    {
        var key = new DocumentKey(incoming.Pk, incoming.Id);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await container.GetAsync<T>(key, context);
            if (current != null && incoming.CreationDate <= current.CreationDate) return;
            var updated = incoming with { ETag = current?.ETag };
            var uow = container.CreateUnitOfWork(key.Pk);
            if (current == null) uow.Create(updated); else uow.Update(updated);
            try { await uow.SaveChangesAsync(context); return; }
            catch (UnitOfWorkException ex) when (IsConcurrencyFailure(ex)) { }
        }
    }
}
