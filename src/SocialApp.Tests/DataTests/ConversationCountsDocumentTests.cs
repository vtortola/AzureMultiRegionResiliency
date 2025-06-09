using System.Text.Json;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.User;

namespace SocialApp.Tests.DataTests;

public class ConversationCountsDocumentTests
{
    [Test]
    public void MergeCounts_PreservesSimultaneousIncrementsFromSharedState()
    {
        var east = CreateCounts("eastus2", 6, 0, 6, 0, 6, 0);
        var west = CreateCounts("eastus2", 5, 0, 5, 0, 5, 0)
            .MergeCounts(CreateCounts("westus2", 1, 0, 1, 0, 1, 0));

        var merged = east.MergeCounts(west);

        Assert.That(merged.GetLikeCount(), Is.EqualTo(7));
        Assert.That(merged.GetCommentCount(), Is.EqualTo(7));
        Assert.That(merged.GetViewCount(), Is.EqualTo(7));
    }

    [Test]
    public void MergeCounts_PreservesIncrementsAndDecrements()
    {
        var east = CreateCounts("eastus2", 8, 2, 6, 1, 9, 2);
        var west = CreateCounts("eastus2", 5, 1, 4, 0, 7, 0)
            .MergeCounts(CreateCounts("westus2", 2, 3, 1, 2, 4, 1));

        var merged = east.MergeCounts(west);

        Assert.That(merged.GetLikeCount(), Is.EqualTo(5));
        Assert.That(merged.GetCommentCount(), Is.EqualTo(4));
        Assert.That(merged.GetViewCount(), Is.EqualTo(10));
        Assert.That(merged.LikeDecrements["eastus2"], Is.EqualTo(2));
        Assert.That(merged.LikeDecrements["westus2"], Is.EqualTo(3));
    }

    [Test]
    public void MergeCounts_IsCommutative()
    {
        var versions = CreateConflictingVersions();
        foreach (var first in versions)
        foreach (var second in versions)
            AssertSameCounts(first.MergeCounts(second), second.MergeCounts(first));
    }

    [Test]
    public void MergeCounts_IsAssociativeAcrossThreeRegions()
    {
        var versions = CreateConflictingVersions();
        foreach (var first in versions)
        foreach (var second in versions)
        foreach (var third in versions)
            AssertSameCounts(first.MergeCounts(second).MergeCounts(third), first.MergeCounts(second.MergeCounts(third)));

        var merged = versions[0].MergeCounts(versions[1]).MergeCounts(versions[2]);
        Assert.That(merged.GetLikeCount(), Is.EqualTo(7));
        Assert.That(merged.GetCommentCount(), Is.EqualTo(9));
        Assert.That(merged.GetViewCount(), Is.EqualTo(12));
    }

    [Test]
    public void MergeCounts_IsIdempotentWithRepeatedAndOlderConflicts()
    {
        var versions = CreateConflictingVersions();
        var merged = versions[0].MergeCounts(versions[1]).MergeCounts(versions[2]);

        AssertSameCounts(merged, merged.MergeCounts(merged));
        foreach (var version in versions)
        {
            AssertSameCounts(version, version.MergeCounts(version));
            AssertSameCounts(merged, merged.MergeCounts(version).MergeCounts(version));
        }
    }

    [Test]
    public void MergeCounts_DoesNotChangeInputContributions()
    {
        var local = CreateCounts("eastus2", 2, 1, 3, 1, 4, 1);
        var remote = CreateCounts("westus2", 3, 2, 4, 2, 5, 2);
        var localJson = JsonSerializer.Serialize(local, DocumentSerialization.Options);
        var remoteJson = JsonSerializer.Serialize(remote, DocumentSerialization.Options);

        var merged = local.MergeCounts(remote);
        merged.LikeIncrements["eastus2"]++;
        merged.CommentDecrements["westus2"]++;

        Assert.That(JsonSerializer.Serialize(local, DocumentSerialization.Options), Is.EqualTo(localJson));
        Assert.That(JsonSerializer.Serialize(remote, DocumentSerialization.Options), Is.EqualTo(remoteJson));
    }

    [Test]
    public void NewCounts_PersistEmptyMapsForAtomicIncrements()
    {
        var counts = new ConversationCountsDocument("user", "conversation", null, null, "local");
        var json = JsonSerializer.SerializeToElement(counts, DocumentSerialization.Options);

        foreach (var counter in new[] { "like", "comment", "view" })
        foreach (var direction in new[] { "Increments", "Decrements" })
        {
            var contributions = json.GetProperty(counter + direction);
            Assert.That(contributions.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(contributions.EnumerateObject().Count(), Is.Zero);
        }

        Assert.That(counts.NoCounterIsZero(), Is.False);
    }

    [Test]
    public void Serialization_RecalculatesTotalsFromContributions()
    {
        var counts = CreateCounts("westus2", 0, 2, 1, 3, 0, 1);
        var json = JsonSerializer.SerializeToElement(counts, DocumentSerialization.Options);
        var restored = (ConversationCountsDocument)DocumentSerialization.DeserializeDocument(json)!;

        AssertSameCounts(counts, restored);
        Assert.That(restored.ProcessingRegion, Is.EqualTo(counts.ProcessingRegion));
        Assert.That(restored.GetLikeCount(), Is.EqualTo(-2));
        Assert.That(restored.GetCommentCount(), Is.EqualTo(-2));
        Assert.That(restored.GetViewCount(), Is.EqualTo(-1));
        Assert.That(json.TryGetProperty("likeCount", out _), Is.False);
        Assert.That(json.TryGetProperty("commentCount", out _), Is.False);
        Assert.That(json.TryGetProperty("viewCount", out _), Is.False);
    }

    [TestCase(1, 2)]
    [TestCase(3, 0)]
    public void CommentAndFeedCopies_DeriveTotalsFromRegionalContributions(int decrements, int expected)
    {
        var counts = CreateCounts("eastus2", 2, 0, 2, 0, 2, 0)
            .MergeCounts(CreateCounts("westus2", 1, decrements, 1, decrements, 1, decrements)) with
        {
            ParentConversationUserId = "parent-user",
            ParentConversationId = "parent-conversation"
        };

        var comment = CommentCountsDocument.TryGenerateParentCommentCounts(counts);
        var feed = FeedConversationCountsDocument.From("follower", counts);

        Assert.That(counts.NoCounterIsZero(), Is.True);
        AssertSameCounts(counts, comment!);
        AssertSameCounts(counts, feed);
        Assert.That(comment!.GetLikeCount(), Is.EqualTo(expected));
        Assert.That(comment.GetCommentCount(), Is.EqualTo(expected));
        Assert.That(comment.GetViewCount(), Is.EqualTo(expected));
        Assert.That(feed.GetLikeCount(), Is.EqualTo(expected));
        Assert.That(feed.GetCommentCount(), Is.EqualTo(expected));
        Assert.That(feed.GetViewCount(), Is.EqualTo(expected));
    }

    [Test]
    public void AllCounterDocuments_SerializeRegionalMapsWithoutScalarTotals()
    {
        var source = CreateCounts("eastus2", 5, 1, 3, 1, 8, 2)
            .MergeCounts(CreateCounts("westus2", 3, 2, 4, 2, 6, 1)) with
        {
            ParentConversationUserId = "parent-user",
            ParentConversationId = "parent-conversation"
        };
        ContentCountsDocument[] documents =
        [
            source,
            CommentCountsDocument.TryGenerateParentCommentCounts(source)!,
            FeedConversationCountsDocument.From("follower", source)
        ];

        foreach (var document in documents)
        {
            var json = JsonSerializer.SerializeToElement(document, document.GetType(), DocumentSerialization.Options);
            var restored = (ContentCountsDocument)DocumentSerialization.DeserializeDocument(json)!;

            Assert.That(restored.GetType(), Is.EqualTo(document.GetType()));
            Assert.That(restored.Id, Is.EqualTo(document.Id));
            Assert.That(restored.Pk, Is.EqualTo(document.Pk));
            AssertSameCounts(source, restored);
            foreach (var counter in new[] { "like", "comment", "view" })
            {
                Assert.That(json.GetProperty(counter + "Increments").ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(json.GetProperty(counter + "Decrements").ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(json.TryGetProperty(counter + "Count", out _), Is.False);
            }
        }
    }

    [Test]
    public void CommentAndFeedCopies_DoNotShareMutableContributions()
    {
        var source = CreateCounts("eastus2", 5, 1, 3, 1, 8, 2) with
        {
            ParentConversationUserId = "parent-user",
            ParentConversationId = "parent-conversation"
        };
        var original = JsonSerializer.Serialize(source, DocumentSerialization.Options);
        var comment = CommentCountsDocument.TryGenerateParentCommentCounts(source)!;
        var feed = FeedConversationCountsDocument.From("follower", source);

        comment.LikeIncrements["eastus2"]++;
        comment.CommentIncrements["eastus2"]++;
        comment.ViewIncrements["eastus2"]++;
        feed.LikeDecrements["eastus2"]++;
        feed.CommentDecrements["eastus2"]++;
        feed.ViewDecrements["eastus2"]++;

        Assert.That(JsonSerializer.Serialize(source, DocumentSerialization.Options), Is.EqualTo(original));
        Assert.That(feed.LikeIncrements, Is.EquivalentTo(source.LikeIncrements));
        Assert.That(feed.CommentIncrements, Is.EquivalentTo(source.CommentIncrements));
        Assert.That(feed.ViewIncrements, Is.EquivalentTo(source.ViewIncrements));
        Assert.That(comment.LikeDecrements, Is.EquivalentTo(source.LikeDecrements));
        Assert.That(comment.CommentDecrements, Is.EquivalentTo(source.CommentDecrements));
        Assert.That(comment.ViewDecrements, Is.EquivalentTo(source.ViewDecrements));
    }

    private static ConversationCountsDocument[] CreateConflictingVersions()
        =>
        [
            CreateCounts("eastus2", 5, 2, 6, 1, 7, 0),
            CreateCounts("eastus2", 4, 1, 4, 0, 3, 0)
                .MergeCounts(CreateCounts("westus2", 3, 1, 2, 1, 4, 2)),
            CreateCounts("westus2", 2, 0, 1, 0, 2, 1)
                .MergeCounts(CreateCounts("northeurope", 4, 2, 5, 2, 6, 3))
        ];

    private static ConversationCountsDocument CreateCounts(string region, int likes, int removedLikes, int comments, int removedComments, int views, int removedViews)
        => new("user", "conversation", null, null, region)
        {
            LikeIncrements = new() { [region] = likes },
            LikeDecrements = new() { [region] = removedLikes },
            CommentIncrements = new() { [region] = comments },
            CommentDecrements = new() { [region] = removedComments },
            ViewIncrements = new() { [region] = views },
            ViewDecrements = new() { [region] = removedViews }
        };

    private static void AssertSameCounts(ContentCountsDocument expected, ContentCountsDocument actual)
    {
        Assert.That(actual.LikeIncrements, Is.EquivalentTo(expected.LikeIncrements));
        Assert.That(actual.LikeDecrements, Is.EquivalentTo(expected.LikeDecrements));
        Assert.That(actual.CommentIncrements, Is.EquivalentTo(expected.CommentIncrements));
        Assert.That(actual.CommentDecrements, Is.EquivalentTo(expected.CommentDecrements));
        Assert.That(actual.ViewIncrements, Is.EquivalentTo(expected.ViewIncrements));
        Assert.That(actual.ViewDecrements, Is.EquivalentTo(expected.ViewDecrements));
        Assert.That(actual.GetLikeCount(), Is.EqualTo(expected.GetLikeCount()));
        Assert.That(actual.GetCommentCount(), Is.EqualTo(expected.GetCommentCount()));
        Assert.That(actual.GetViewCount(), Is.EqualTo(expected.GetViewCount()));
    }
}
