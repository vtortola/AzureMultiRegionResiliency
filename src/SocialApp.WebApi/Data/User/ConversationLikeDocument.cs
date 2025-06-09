using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public record ConversationLikeDocument(string ConversationUserId, string ConversationId, string UserId, bool Like) 
    : Document(Key(ConversationUserId, ConversationId, UserId)), IRegionOwnedDocument
{
    public DateTime CreationDate { get; set; }

    public string ProcessingRegion { get; init; } = "";
    public static DocumentKey Key(string conversationUserId, string conversationId, string userId)
    {
        var pk = "user:"+conversationUserId;
        var id = $"like:{conversationId}:user:{userId}";
        return new DocumentKey(pk, id);
    }
}