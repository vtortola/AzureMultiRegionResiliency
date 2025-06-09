using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public record ConversationViewDocument(string UserId, string ConversationId, string EventId, string ProcessingRegion)
    : Document(new DocumentKey("user:" + UserId, $"view:{ConversationId}:{EventId}")), IRegionOwnedDocument
{
    public DateTime CreationDate { get; set; }
    public bool Applied { get; init; }
}
