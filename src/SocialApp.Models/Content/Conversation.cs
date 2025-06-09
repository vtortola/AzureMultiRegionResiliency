namespace SocialApp.Models.Content;

public class Conversation
{
    public ConversationRoot Root { get; set; } = new();
    public List<ConversationComment> LastComments { get; set; } = [];
}
