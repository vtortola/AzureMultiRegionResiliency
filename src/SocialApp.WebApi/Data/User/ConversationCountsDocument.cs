using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public record ConversationCountsDocument : ContentCountsDocument
{
    public string UserId { get; init; }
    public string ConversationId { get; init; }
    public string? ParentConversationUserId { get; init; }
    public string? ParentConversationId { get; init; }
    public DateTime SourceCreationDate { get; init; }
    public bool IsRootConversation { get; set; }
    public ConversationCountsDocument(string userId, string conversationId, string? parentConversationUserId,
        string? parentConversationId, string processingRegion) : base(Key(userId, conversationId))
    {
        UserId = userId;
        ConversationId = conversationId;
        ParentConversationUserId = parentConversationUserId;
        ParentConversationId = parentConversationId;
        ProcessingRegion = processingRegion;
    }
    public static DocumentKey Key(string userId, string conversationId)
        => new("user:" + userId, $"conversation:{conversationId}:z_counts");
}
