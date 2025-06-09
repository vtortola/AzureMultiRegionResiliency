using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public record FeedConversationCountsDocument(string FeedUserId, string ConversationUserId, string ConversationId)
    : ContentCountsDocument(Key(FeedUserId, ConversationUserId, ConversationId))
{
    public static FeedConversationCountsDocument From(string feedUserId, ConversationCountsDocument conversation)
        => new(feedUserId, conversation.UserId, conversation.ConversationId)
        {
            LikeCount = conversation.LikeCount,
            CommentCount = conversation.CommentCount,
            ViewCount = conversation.ViewCount,
            CreationDate = conversation.CreationDate,
            ProcessingRegion = conversation.ProcessingRegion,
            Deleted = conversation.Deleted
        };

    public static DocumentKey Key(string feedUserId, string conversationUserId, string conversationId)
    {
        var pk = "user:"+feedUserId;
        var id = $"feed:{conversationId}:user:{conversationUserId}:counts";
        return new DocumentKey(pk, id);
    }
}
