using Microsoft.Extensions.Logging.Abstractions;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Content.Containers;
using SocialApp.WebApi.Features.Content.Services;

namespace SocialApp.Tests.ServicesTests;

public class ConversationCounterStorageTests : ServiceTestsBase
{
    [Test]
    public async Task ConcurrentAndRepeatedReactions_CountOnce_AndRejectOlderLikes()
    {
        var (contents, post) = await CreatePostAsync();
        var like = new ConversationUserLikeDocument("alice", post.UserId, post.ConversationId, true, null, null, post.ProcessingRegion)
            { CreationDate = new DateTime(10, DateTimeKind.Utc) };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => contents.SetConversationLikeAsync(like, OperationContext.New())));
        Assert.That((await CountsAsync(contents, post)).LikeCount, Is.EqualTo(1));
        var unlike = like with { Like = false, CreationDate = new DateTime(11, DateTimeKind.Utc) };
        await contents.SetConversationLikeAsync(unlike, OperationContext.New());
        await contents.SetConversationLikeAsync(like, OperationContext.New());
        await contents.SetConversationLikeAsync(unlike, OperationContext.New());
        Assert.That((await CountsAsync(contents, post)).LikeCount, Is.Zero);
        var target = await contents.GetAsync<ConversationLikeDocument>(ConversationLikeDocument.Key(post.UserId, post.ConversationId, "alice"), OperationContext.New());
        Assert.That(target!.CreationDate, Is.EqualTo(unlike.CreationDate));
        Assert.That(target.Ttl, Is.EqualTo(-1));
    }

    [Test]
    public async Task ViewEventReplay_AfterCommitBeforeCheckpoint_CountsOnce()
    {
        var (contents, post) = await CreatePostAsync();
        var view = new ConversationViewDocument(post.UserId, post.ConversationId, "view", post.ProcessingRegion);
        var uow = contents.CreateUnitOfWork(view.Pk);
        uow.Create(view);
        await uow.SaveChangesAsync(OperationContext.New());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => contents.ApplyViewAsync(view, OperationContext.New())));
        // Replacement instance replays from a checkpoint preceding the committed view.
        await new ContentContainer(UserDatabase).ApplyViewAsync(view, OperationContext.New());
        Assert.That((await CountsAsync(contents, post)).ViewCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ConflictResolution_UsesAssignedRegionAndCreationDate_NotDeploymentRegion()
    {
        var (contents, post) = await CreatePostAsync();
        var merger = new ContentConflictMerger(contents, NullLogger<ContentConflictResolutionService>.Instance, post.ProcessingRegion);
        var newer = post with { Content = "winner", CreationDate = new DateTime(20, DateTimeKind.Utc) };
        Assert.That(await merger.MergeAsync(newer, post, OperationContext.New()), Is.True);
        var current = (await contents.GetAsync<ConversationDocument>(ConversationDocument.Key(post.UserId, post.ConversationId), OperationContext.New()))!;
        Assert.That(current.Content, Is.EqualTo("winner"));
        Assert.That(await merger.MergeAsync(post, current, OperationContext.New()), Is.True);
        var foreign = new ContentConflictMerger(contents, NullLogger<ContentConflictResolutionService>.Instance, "unassigned");
        Assert.That(await foreign.MergeAsync(newer, current, OperationContext.New()), Is.False);
        Assert.That((await contents.GetAsync<ConversationDocument>(ConversationDocument.Key(post.UserId, post.ConversationId), OperationContext.New()))!.Content, Is.EqualTo("winner"));
    }

    [Test]
    public async Task CounterConflict_KeepsNewerSnapshot_WithoutAddingTotals()
    {
        var (contents, post) = await CreatePostAsync();
        var current = await CountsAsync(contents, post);
        var incoming = current with
        {
            ETag = null, LikeCount = 7,
            CreationDate = current.CreationDate.AddTicks(1)
        };
        var merger = new ContentConflictMerger(contents, NullLogger<ContentConflictResolutionService>.Instance, post.ProcessingRegion);
        Assert.That(await merger.MergeAsync(incoming, current, OperationContext.New()), Is.True);
        Assert.That((await CountsAsync(contents, post)).LikeCount, Is.EqualTo(7));
        Assert.That(await merger.MergeAsync(current, incoming, OperationContext.New()), Is.True);
        Assert.That((await CountsAsync(contents, post)).LikeCount, Is.EqualTo(7));
    }

    [Test]
    public async Task WinningSourceConflict_IsAppliedOnce_WithoutDirectCounterCorrection()
    {
        var (contents, post) = await CreatePostAsync();
        var liked = new ConversationUserLikeDocument(Guid.NewGuid().ToString("N"), post.UserId, post.ConversationId,
            true, null, null, post.ProcessingRegion) { CreationDate = new DateTime(10, DateTimeKind.Utc) };
        var uow = contents.CreateUnitOfWork(liked.Pk);
        uow.Create(liked);
        await uow.SaveChangesAsync(OperationContext.New());
        await contents.SetConversationLikeAsync(liked, OperationContext.New());
        var unliked = liked with { Like = false, CreationDate = new DateTime(11, DateTimeKind.Utc) };
        var merger = new ContentConflictMerger(contents, NullLogger<ContentConflictResolutionService>.Instance, post.ProcessingRegion);
        await merger.MergeAsync(unliked, liked, OperationContext.New());
        Assert.That((await CountsAsync(contents, post)).LikeCount, Is.EqualTo(1), "Resolution changes source intent only.");
        var winner = (await contents.GetAsync<ConversationUserLikeDocument>(new(liked.Pk, liked.Id), OperationContext.New()))!;
        await contents.SetConversationLikeAsync(winner, OperationContext.New());
        await contents.SetConversationLikeAsync(liked, OperationContext.New());
        await contents.SetConversationLikeAsync(winner, OperationContext.New());
        Assert.That((await CountsAsync(contents, post)).LikeCount, Is.Zero);
    }

    private async Task<(ContentContainer, ConversationDocument)> CreatePostAsync()
    {
        var contents = new ContentContainer(UserDatabase);
        var post = new ConversationDocument(Guid.NewGuid().ToString("N"), "post", "content", DateTime.UtcNow, 0, null, null, "manual-test")
            { IsRootConversation = true, CreationDate = new DateTime(1, DateTimeKind.Utc) };
        var uow = contents.CreateUnitOfWork(post.Pk);
        uow.Create(post);
        await uow.SaveChangesAsync(OperationContext.New());
        await contents.EnsureCountsAsync(post, OperationContext.New());
        return (contents, post);
    }

    private static async Task<ConversationCountsDocument> CountsAsync(ContentContainer contents, ConversationDocument post)
        => (await contents.GetAsync<ConversationCountsDocument>(ConversationCountsDocument.Key(post.UserId, post.ConversationId), OperationContext.New()))!;
}
