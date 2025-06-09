using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public abstract record ContentCountsDocument : Document
{
    protected ContentCountsDocument(DocumentKey key)
        :base(key) { }

    public Dictionary<string, int> LikeIncrements { get; init; } = new();
    public Dictionary<string, int> LikeDecrements { get; init; } = new();
    public Dictionary<string, int> CommentIncrements { get; init; } = new();
    public Dictionary<string, int> CommentDecrements { get; init; } = new();
    public Dictionary<string, int> ViewIncrements { get; init; } = new();
    public Dictionary<string, int> ViewDecrements { get; init; } = new();

    public int GetLikeCount() => LikeIncrements.Values.Sum() - LikeDecrements.Values.Sum();
    public int GetCommentCount() => CommentIncrements.Values.Sum() - CommentDecrements.Values.Sum();
    public int GetViewCount() => ViewIncrements.Values.Sum() - ViewDecrements.Values.Sum();

    public bool NoCounterIsZero()
        => LikeIncrements.Count != 0 || LikeDecrements.Count != 0
            || CommentIncrements.Count != 0 || CommentDecrements.Count != 0
            || ViewIncrements.Count != 0 || ViewDecrements.Count != 0;
}
