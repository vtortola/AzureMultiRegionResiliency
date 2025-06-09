using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public record CommentCountsDocument(string ConversationUserId, string ConversationId, string UserId, string CommentId)
    : ContentCountsDocument(Key(ConversationUserId, ConversationId, CommentId))
{
    public static DocumentKey Key(string conversationUserId, string conversationId, string commentId)
    {
        var pk = "user:"+conversationUserId;
        var id = $"conversation:{conversationId}:comment:{commentId}:counts";
        return new DocumentKey(pk, id);
    }

    public static CommentCountsDocument? TryGenerateParentCommentCounts(ConversationCountsDocument tcounts)
        => string.IsNullOrWhiteSpace(tcounts.ParentConversationId) 
            ? null 
            : new (tcounts.ParentConversationUserId ?? throw new InvalidOperationException("Missing parent conversation user ID."), tcounts.ParentConversationId, tcounts.UserId, tcounts.ConversationId)
                {
                    LikeCount = tcounts.LikeCount,
                    CommentCount = tcounts.CommentCount,
                    ViewCount = tcounts.ViewCount,
                    CreationDate = tcounts.CreationDate,
                    ProcessingRegion = tcounts.ProcessingRegion,
                    Deleted = tcounts.Deleted,
                    Ttl = tcounts.Ttl
                };
}
