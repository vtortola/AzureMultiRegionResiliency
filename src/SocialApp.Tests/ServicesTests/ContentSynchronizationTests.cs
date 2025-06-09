using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Content.Containers;

namespace SocialApp.Tests.ServicesTests;

public class ContentSynchronizationTests : ServiceTestsBase
{
    [Test]
    public async Task RepeatedCreationDeletionAndDelayedCreation_ChangeParentCountOnce()
    {
        var (contents, parent, child) = await CreateCommentAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => contents.SyncParentCommentAsync(child, OperationContext.New())));
        var key = ConversationCountsDocument.Key(parent.UserId, parent.ConversationId);
        Assert.That((await contents.GetAsync<ConversationCountsDocument>(key, OperationContext.New()))!.CommentCount, Is.EqualTo(1));
        var deleted = child with { Deleted = true, CreationDate = new DateTime(3, DateTimeKind.Utc) };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => contents.SyncParentCommentAsync(deleted, OperationContext.New())));
        await contents.SyncParentCommentAsync(child, OperationContext.New());
        Assert.That((await contents.GetAsync<ConversationCountsDocument>(key, OperationContext.New()))!.CommentCount, Is.Zero);
        var comment = await contents.GetAsync<CommentDocument>(CommentDocument.Key(parent.UserId, parent.ConversationId, child.ConversationId), OperationContext.New());
        Assert.That(comment!.Deleted, Is.True);
        Assert.That(comment.Ttl, Is.EqualTo(-1));
    }

    [Test]
    public async Task DeletionBeforeCreation_DoesNotDecrementOrResurrect()
    {
        var (contents, parent, child) = await CreateCommentAsync();
        await contents.SyncParentCommentAsync(child with { Deleted = true, CreationDate = new DateTime(3, DateTimeKind.Utc) }, OperationContext.New());
        await contents.SyncParentCommentAsync(child, OperationContext.New());
        Assert.That((await contents.GetAsync<ConversationCountsDocument>(ConversationCountsDocument.Key(parent.UserId, parent.ConversationId), OperationContext.New()))!.CommentCount, Is.Zero);
    }

    [Test]
    public async Task CounterProjections_IgnoreOlderSnapshots_IncludingAfterDeletion()
    {
        var (contents, _, child) = await CreateCommentAsync();
        var older = child.CreateCounts() with { LikeCount = 10, CreationDate = new DateTime(1, DateTimeKind.Utc) };
        var newer = older with { LikeCount = 2, ViewCount = 4, CreationDate = new DateTime(2, DateTimeKind.Utc) };
        await contents.MergeParentCommentCountsAsync(newer, OperationContext.New());
        await contents.MergeParentCommentCountsAsync(older, OperationContext.New());
        var key = CommentCountsDocument.Key(child.ParentConversationUserId!, child.ParentConversationId!, child.ConversationId);
        var counts = (await contents.GetAsync<CommentCountsDocument>(key, OperationContext.New()))!;
        Assert.That(counts.LikeCount, Is.EqualTo(2));
        Assert.That(counts.ViewCount, Is.EqualTo(4));
        await contents.MergeParentCommentCountsAsync(newer with { Deleted = true, CreationDate = new DateTime(3, DateTimeKind.Utc) }, OperationContext.New());
        await contents.MergeParentCommentCountsAsync(newer, OperationContext.New());
        Assert.That((await contents.GetAsync<CommentCountsDocument>(key, OperationContext.New()))!.Deleted, Is.True);
    }

    private async Task<(ContentContainer, ConversationDocument, ConversationDocument)> CreateCommentAsync()
    {
        var contents = new ContentContainer(UserDatabase);
        var parent = new ConversationDocument(Guid.NewGuid().ToString("N"), "parent", "root", DateTime.UtcNow, 0, null, null, "manual-test")
            { IsRootConversation = true, CreationDate = new DateTime(1, DateTimeKind.Utc) };
        var child = new ConversationDocument(Guid.NewGuid().ToString("N"), "child", "comment", DateTime.UtcNow, 0, parent.UserId, parent.ConversationId, parent.ProcessingRegion)
            { CreationDate = new DateTime(2, DateTimeKind.Utc) };
        foreach (var doc in new[] { parent, child })
        {
            var uow = contents.CreateUnitOfWork(doc.Pk);
            uow.Create(doc);
            await uow.SaveChangesAsync(OperationContext.New());
        }
        return (contents, parent, child);
    }
}
