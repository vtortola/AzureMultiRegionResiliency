using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public record ConversationCountsDocument(string UserId, string ConversationId, string? ParentConversationUserId, string? ParentConversationId, string ProcessingRegion)
    : ContentCountsDocument(Key(UserId, ConversationId)), IRegionOwnedDocument
{
    public bool IsRootConversation {get; set;}

    public static DocumentKey Key(string userId, string conversationId)
    {
        var pk = "user:"+userId;
        var id = $"conversation:{conversationId}:z_counts";
        return new DocumentKey(pk, id);
    }

    public ConversationCountsDocument MergeCounts(ConversationCountsDocument other)
        => this with
        {
            LikeIncrements = MergeContributions(LikeIncrements, other.LikeIncrements),
            LikeDecrements = MergeContributions(LikeDecrements, other.LikeDecrements),
            CommentIncrements = MergeContributions(CommentIncrements, other.CommentIncrements),
            CommentDecrements = MergeContributions(CommentDecrements, other.CommentDecrements),
            ViewIncrements = MergeContributions(ViewIncrements, other.ViewIncrements),
            ViewDecrements = MergeContributions(ViewDecrements, other.ViewDecrements)
        };

    private static Dictionary<string, int> MergeContributions(Dictionary<string, int> local, Dictionary<string, int> remote)
    {
        var merged = new Dictionary<string, int>(local);
        foreach (var (region, contribution) in remote)
            merged[region] = Math.Max(merged.GetValueOrDefault(region), contribution);
        return merged;
    }
}
