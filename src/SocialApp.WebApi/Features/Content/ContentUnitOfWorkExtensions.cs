using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;

namespace SocialApp.WebApi.Features.Content;

public static class ContentUnitOfWorkExtensions
{
    public static void IncrementConversationLikes(this IUnitOfWork uow, DocumentKey key, int increment = 1)
        => uow.Increment<ConversationCountsDocument>(key,
            c => c.LikeCount, increment);

    public static void IncrementCommentLikes(this IUnitOfWork uow, DocumentKey key, int increment = 1)
        => uow.Increment<CommentCountsDocument>(key,
            c => c.LikeCount, increment);

    public static void IncrementConversationComments(this IUnitOfWork uow, DocumentKey key, int increment = 1)
        => uow.Increment<ConversationCountsDocument>(key,
            c => c.CommentCount, increment);

    public static void IncrementConversationViews(this IUnitOfWork uow, DocumentKey key, int increment = 1)
        => uow.Increment<ConversationCountsDocument>(key,
            c => c.ViewCount, increment);
}
