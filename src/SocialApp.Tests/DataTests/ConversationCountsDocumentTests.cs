using System.Text.Json;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.User;

namespace SocialApp.Tests.DataTests;

public class ConversationCountsDocumentTests
{
    [Test]
    public void CountersAndProjections_RoundTripIntegersOwnershipAndCreationDate()
    {
        var source = new ConversationCountsDocument("author", "post", "parent", "thread", "eastus2")
        {
            LikeCount = 5, CommentCount = 3, ViewCount = 8, CreationDate = new DateTime(12, DateTimeKind.Utc)
        };
        ContentCountsDocument[] documents = [source, CommentCountsDocument.TryGenerateParentCommentCounts(source)!, FeedConversationCountsDocument.From("reader", source)];
        foreach (var document in documents)
        {
            var json = JsonSerializer.SerializeToElement(document, document.GetType(), DocumentSerialization.Options);
            var restored = (ContentCountsDocument)DocumentSerialization.DeserializeDocument(json)!;
            Assert.Multiple(() =>
            {
                Assert.That(restored.LikeCount, Is.EqualTo(5));
                Assert.That(restored.CommentCount, Is.EqualTo(3));
                Assert.That(restored.ViewCount, Is.EqualTo(8));
                Assert.That(restored.CreationDate, Is.EqualTo(source.CreationDate));
                Assert.That(restored.ProcessingRegion, Is.EqualTo("eastus2"));
                Assert.That(json.GetProperty("likeCount").ValueKind, Is.EqualTo(JsonValueKind.Number));
                Assert.That(json.TryGetProperty("likeIncrements", out _), Is.False);
            });
        }
    }

    [Test]
    public void MissingIntegerFields_DefaultToZero()
    {
        var source = new ConversationCountsDocument("author", "post", null, null, "eastus2");
        var restored = (ConversationCountsDocument)DocumentSerialization.DeserializeDocument(JsonSerializer.SerializeToElement(source, DocumentSerialization.Options))!;
        Assert.That(restored.GetLikeCount(), Is.Zero);
        Assert.That(restored.GetCommentCount(), Is.Zero);
        Assert.That(restored.GetViewCount(), Is.Zero);
    }

    [Test]
    public void CreationDate_IsMutable_AndDoesNotApplyToAccountDocuments()
    {
        var counts = new ConversationCountsDocument("author", "post", null, null, "eastus2");
        var now = DateTime.UtcNow;
        counts.CreationDate = now;
        Assert.That(counts.CreationDate, Is.EqualTo(now));
        Assert.That(typeof(Document).GetProperty("CreationDate"), Is.Null);
        Assert.That(typeof(Document).GetProperty("Revision"), Is.Null);
    }
}
