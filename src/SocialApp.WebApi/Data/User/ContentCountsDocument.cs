using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.User;

public abstract record ContentCountsDocument : Document, IRegionOwnedDocument
{
    public DateTime CreationDate { get; set; }

    protected ContentCountsDocument(DocumentKey key) : base(key) { }
    public string ProcessingRegion { get; init; } = "";
    public int LikeCount { get; init; }
    public int CommentCount { get; init; }
    public int ViewCount { get; init; }
    public int GetLikeCount() => LikeCount;
    public int GetCommentCount() => CommentCount;
    public int GetViewCount() => ViewCount;
    public bool NoCounterIsZero() => LikeCount != 0 || CommentCount != 0 || ViewCount != 0;
}
