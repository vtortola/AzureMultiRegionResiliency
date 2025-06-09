using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Content;
using SocialApp.WebApi.Features.Content.Containers;
using SocialApp.WebApi.Features.Content.Services;

namespace SocialApp.Tests.ServicesTests;

public class ConversationCounterStorageTests : ServiceTestsBase
{
    [Test]
    public async Task ConcurrentRegionalWrites_PreserveEveryIncrementAndDecrement()
    {
        var contents = new ContentContainer(UserDatabase);
        var context = OperationContext.New();
        var counts = await CreateCountsAsync(contents, context);
        var key = ConversationCountsDocument.Key(counts.UserId, counts.ConversationId);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var uow = contents.CreateUnitOfWork(key.Pk);
            uow.IncrementConversationLikes(key, i % 2 == 0 ? 1 : -1);
            uow.IncrementConversationComments(key, i % 2 == 0 ? 1 : -1);
            uow.IncrementConversationViews(key);
            await uow.SaveChangesAsync(CreateConcurrentContext(context));
        }));

        var current = await contents.GetAsync<ConversationCountsDocument>(key, context);
        Assert.That(current!.LikeIncrements[UserDatabase.Region], Is.EqualTo(10));
        Assert.That(current.LikeDecrements[UserDatabase.Region], Is.EqualTo(10));
        Assert.That(current.CommentIncrements[UserDatabase.Region], Is.EqualTo(10));
        Assert.That(current.CommentDecrements[UserDatabase.Region], Is.EqualTo(10));
        Assert.That(current.ViewIncrements[UserDatabase.Region], Is.EqualTo(20));
        Assert.That(current.GetLikeCount(), Is.Zero);
        Assert.That(current.GetCommentCount(), Is.Zero);
        Assert.That(current.GetViewCount(), Is.EqualTo(20));

        var stored = await UserDatabase.GetContainer("contents").ReadItemAsync<JsonElement>(key.Id, new PartitionKey(key.Pk));
        Assert.That(stored.Resource.TryGetProperty("likeCount", out _), Is.False);
        Assert.That(stored.Resource.TryGetProperty("commentCount", out _), Is.False);
        Assert.That(stored.Resource.TryGetProperty("viewCount", out _), Is.False);
    }

    [Test]
    public async Task ConcurrentCommentLikes_PreserveRegionalIncrementsAndDecrements()
    {
        var contents = new ContentContainer(UserDatabase);
        var context = OperationContext.New();
        var counts = new CommentCountsDocument(Guid.NewGuid().ToString("N"), "conversation", "author", "comment");
        var key = CommentCountsDocument.Key(counts.ConversationUserId, counts.ConversationId, counts.CommentId);
        var uow = contents.CreateUnitOfWork(key.Pk);
        uow.Create(counts);
        await uow.SaveChangesAsync(context);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var like = i % 2 == 0;
            var reaction = new CommentLikeDocument(counts.ConversationUserId, counts.ConversationId, counts.CommentId, i.ToString(), like);
            var reactionUow = contents.CreateUnitOfWork(key.Pk);
            reactionUow.CreateOrUpdate(reaction);
            reactionUow.IncrementCommentLikes(key, like ? 1 : -1);
            await reactionUow.SaveChangesAsync(CreateConcurrentContext(context));
        }));

        var current = await contents.GetAsync<CommentCountsDocument>(key, context);
        Assert.That(current!.LikeIncrements[UserDatabase.Region], Is.EqualTo(10));
        Assert.That(current.LikeDecrements[UserDatabase.Region], Is.EqualTo(10));
        Assert.That(current.GetLikeCount(), Is.Zero);
    }

    [Test]
    public async Task ConflictMerge_PreservesNewerStoredContributionsAndRepeatedConflicts()
    {
        var contents = new ContentContainer(UserDatabase);
        var context = OperationContext.New();
        var counts = await CreateCountsAsync(contents, context);
        var key = ConversationCountsDocument.Key(counts.UserId, counts.ConversationId);
        var uow = contents.CreateUnitOfWork(key.Pk);
        uow.IncrementConversationLikes(key, 5);
        await uow.SaveChangesAsync(context);
        var local = await contents.GetAsync<ConversationCountsDocument>(key, context);
        var remote = local! with
        {
            LikeIncrements = new() { [UserDatabase.Region] = 5, ["remote"] = 1 },
            LikeDecrements = new() { ["remote"] = 1 }
        };

        uow = contents.CreateUnitOfWork(key.Pk);
        uow.IncrementConversationLikes(key, 2);
        await uow.SaveChangesAsync(context);

        var merger = new ContentConflictMerger(contents, NullLogger<ContentConflictResolutionService>.Instance);
        Assert.That(await merger.MergeAsync(remote, local, context), Is.True);
        Assert.That(await merger.MergeAsync(local, remote, context), Is.True);
        Assert.That(await merger.MergeAsync(remote, local, context), Is.True);

        var current = await contents.GetAsync<ConversationCountsDocument>(key, context);
        Assert.That(current!.ProcessingRegion, Is.EqualTo(local.ProcessingRegion));
        Assert.That(current!.LikeIncrements[UserDatabase.Region], Is.EqualTo(7));
        Assert.That(current.LikeIncrements["remote"], Is.EqualTo(1));
        Assert.That(current.LikeDecrements["remote"], Is.EqualTo(1));
        Assert.That(current.GetLikeCount(), Is.EqualTo(7));
        Assert.That(current.IsRootConversation, Is.True);
    }

    [Test]
    public async Task ConcurrentConflictMerges_DoNotOverwriteRegionalWrites()
    {
        var contents = new ContentContainer(UserDatabase);
        var context = OperationContext.New();
        var local = await CreateCountsAsync(contents, context);
        var key = ConversationCountsDocument.Key(local.UserId, local.ConversationId);
        var remote = local with { ViewIncrements = new() { ["remote"] = 3 } };
        var merger = new ContentConflictMerger(contents, NullLogger<ContentConflictResolutionService>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var writeContext = CreateConcurrentContext(context);
            var uow = contents.CreateUnitOfWork(key.Pk);
            uow.IncrementConversationViews(key);
            await uow.SaveChangesAsync(writeContext);
            Assert.That(await merger.MergeAsync(remote, local, writeContext), Is.True);
        }));

        var current = await contents.GetAsync<ConversationCountsDocument>(key, context);
        Assert.That(current!.ViewIncrements[UserDatabase.Region], Is.EqualTo(20));
        Assert.That(current.ViewIncrements["remote"], Is.EqualTo(3));
        Assert.That(current.GetViewCount(), Is.EqualTo(23));
    }

    private async Task<ConversationCountsDocument> CreateCountsAsync(ContentContainer contents, OperationContext context)
    {
        var counts = new ConversationCountsDocument(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), null, null, UserDatabase.Region)
        {
            IsRootConversation = true
        };
        var uow = contents.CreateUnitOfWork(counts.Pk);
        uow.Create(counts);
        await uow.SaveChangesAsync(context);
        return counts;
    }

    private static OperationContext CreateConcurrentContext(OperationContext context)
        => new(CancellationToken.None)
        {
            SessionTokens = new Dictionary<string, string?>(context.SessionTokens!)
        };
}
