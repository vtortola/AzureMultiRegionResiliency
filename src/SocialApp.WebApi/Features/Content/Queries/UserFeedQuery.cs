using Microsoft.Azure.Cosmos;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features._Shared.Tuples;

namespace SocialApp.WebApi.Features.Content.Queries;

public sealed class UserFeedQueryMany : IQueryMany<FeedConversationTuple>
{
    public string? UserId { get; set; }
    public string? BeforeConversationId { get; set; }
    public int Limit { get; set; }
}

public sealed class UserFeedQueryHandler :  IQueryManyHandler<UserFeedQueryMany, FeedConversationTuple>
{
    public async IAsyncEnumerable<FeedConversationTuple> ExecuteQueryAsync(CosmoContainer container, UserFeedQueryMany queryMany, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(queryMany.UserId);

        const string sql = @"
            select * 
            from c 
            where c.pk = @pk 
              and c.sk > @start
              and c.sk < @end
              and not is_defined(c.deleted)
            order by c.sk desc 
            offset 0 limit @limit";
        
        var keyStart = FeedConversationDocument.KeyUserFeedStart(queryMany.UserId);
        var cosmos = new QueryDefinition(sql)
            .WithParameter("@pk", keyStart.Pk)
            .WithParameter("@start", keyStart.Id)
            .WithParameter("@end", queryMany.BeforeConversationId == null ? FeedConversationDocument.KeyUserFeedEnd(queryMany.UserId).Id : FeedConversationDocument.KeyUserFeedFrom(queryMany.UserId, queryMany.BeforeConversationId).Id)
            .WithParameter("@limit", queryMany.Limit * 2);

        FeedConversationCountsDocument? counts = null;
        var returned = 0;
        if (queryMany.Limit <= 0) yield break;
        await foreach (var document in container.ExecuteQueryReaderAsync(cosmos, keyStart.Pk, context))
        {
            if(document is FeedConversationDocument doc)
            {
                if (counts?.ConversationId != doc.ConversationId || counts?.ConversationUserId != doc.ConversationUserId)
                    counts = new FeedConversationCountsDocument(doc.FeedUserId, doc.ConversationUserId, doc.ConversationId) { ProcessingRegion = doc.ProcessingRegion };
                yield return new FeedConversationTuple(doc, counts);
                if (++returned >= queryMany.Limit) yield break;
                counts = null;
            }
            else if (document is FeedConversationCountsDocument countDocs)
            {
                counts = countDocs;
            }
            else
            {
                throw new InvalidOperationException("Unexpected document: " + document.GetType().Name);
            }
        }
    }
}
