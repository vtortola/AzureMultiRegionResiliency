using Microsoft.Azure.Cosmos;
using SocialApp.Models.Content;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features._Shared.Tuples;
using SocialApp.WebApi.Features.Account.Services;
using SocialApp.WebApi.Features.Content.Containers;
using SocialApp.WebApi.Features.Content.Exceptions;
using SocialApp.WebApi.Features.Content.Queries;
using SocialApp.WebApi.Features.Session.Models;

namespace SocialApp.WebApi.Features.Content.Services;

public interface IContentService
{
    Task<string> StartConversationAsync(UserSession user, string content, OperationContext context);
    Task<string> CommentAsync(UserSession user, string handle, string conversationId, string content, OperationContext context);
    Task UpdateConversationAsync(UserSession user, string conversationId, string content, OperationContext context);
    Task ReactToConversationAsync(UserSession user, string handle, string conversationId, bool like, OperationContext context);
    Task DeleteConversationAsync(UserSession user, string conversationId, OperationContext context);
    Task<Conversation> GetConversationAsync(string handle, string conversationId, int lastCommentCount, OperationContext context);
    Task<IReadOnlyList<ConversationComment>> GetPreviousCommentsAsync(string handle, string conversationId, string? commentId, int lastCommentCount, OperationContext context);
    Task<IReadOnlyList<ConversationRoot>> GetUserConversationsAsync(string handle, string? beforeConversationId, int limit, OperationContext context);
}

public sealed class ContentService : IContentService
{
    private readonly IQueries _queries;
    private readonly UserDatabase _userDb;
    private readonly IUserHandleService _userHandleService;
    public ContentService(UserDatabase userDb, IUserHandleService userHandleService, IQueries queries)
    {
        _userDb = userDb;
        _userHandleService = userHandleService;
        _queries = queries;
    }

    private ContentContainer GetContentsContainer()
        => new(_userDb);
    
    public async Task<string> StartConversationAsync(UserSession user, string content, OperationContext context)
    {
        try
        {
            var conversationId = Ulid.NewUlid(context.UtcNow).ToString();
            var contents = GetContentsContainer();
            var conversation = new ConversationDocument(user.UserId, conversationId, content, context.UtcNow.UtcDateTime, 0, null, null, _userDb.Region) { IsRootConversation = true, CreationDate = context.UtcNow.UtcDateTime };

            await CreateConversationAsync(contents, conversation, context);
            return conversationId;
        }
        catch (Exception e)
        {
            throw new ContentException(ContentError.UnexpectedError, e);
        }
    }

    private static async Task CreateConversationAsync(ContentContainer contents, ConversationDocument conversation, OperationContext context)
    {
        var uow = contents.CreateUnitOfWork(conversation.Pk);
        uow.Create(conversation);
        await uow.SaveChangesAsync(context);
    }

    public async Task<string> CommentAsync(UserSession user, string handle, string conversationId, string content, OperationContext context)
    {
        try
        {
            var commentId = Ulid.NewUlid(context.UtcNow).ToString();
            var contents = GetContentsContainer();

            var conversationUserId = await _userHandleService.GetUserIdAsync(handle, context);
            
            var parent = await contents.GetAsync<ConversationDocument>(ConversationDocument.Key(conversationUserId, conversationId), context);
            if (parent == null || parent.Deleted) throw new ContentException(ContentError.ContentNotFound);
            var conversation = new ConversationDocument(user.UserId, commentId, content, context.UtcNow.UtcDateTime,
                0, conversationUserId, conversationId, parent.ProcessingRegion)
            {
                CreationDate = context.UtcNow.UtcDateTime
            };
            context.Signal("create-comment-conversation");
            await CreateConversationAsync(contents, conversation, context);

            return commentId;
        }
        catch (Exception e)
        {
            throw new ContentException(ContentError.UnexpectedError, e);
        }
    }

    public async Task UpdateConversationAsync(UserSession user, string conversationId, string content, OperationContext context)
    {
        var contents = GetContentsContainer();
        var key = ConversationDocument.Key(user.UserId, conversationId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await contents.GetAsync<ConversationDocument>(key, context);
            if (current == null || current.Deleted) throw new ContentException(ContentError.ContentNotFound);
            var updated = current with
            {
                Content = content, Version = current.Version + 1, LastModify = context.UtcNow.UtcDateTime,
                CreationDate = ContentContainer.NextCreationDate(current.CreationDate, context)
            };
            context.Signal("update-conversation");
            var uow = contents.CreateUnitOfWork(key.Pk);
            uow.Update(updated);
            try { await uow.SaveChangesAsync(context); return; }
            catch (UnitOfWorkException ex) when (ContentContainer.IsConcurrencyFailure(ex)) { }
        }
    }

    public async Task ReactToConversationAsync(UserSession user, string handle, string conversationId, bool like, OperationContext context)
    {
        var contents = GetContentsContainer();
        var ownerId = await _userHandleService.GetUserIdAsync(handle, context);
        var conversation = await contents.GetAsync<ConversationDocument>(ConversationDocument.Key(ownerId, conversationId), context);
        if (conversation == null || conversation.Deleted) throw new ContentException(ContentError.ContentNotFound);
        var key = ConversationUserLikeDocument.Key(user.UserId, ownerId, conversationId);
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var current = await contents.GetAsync<ConversationUserLikeDocument>(key, context);
            // Even an unlike with no local like is durable intent: another region may have accepted a like.
            var updated = (current ?? new ConversationUserLikeDocument(user.UserId, ownerId, conversationId, like,
                conversation.ParentConversationUserId, conversation.ParentConversationId, conversation.ProcessingRegion)) with
            {
                Like = like, ProcessingRegion = conversation.ProcessingRegion, Ttl = -1,
                CreationDate = ContentContainer.NextCreationDate(current?.CreationDate ?? default, context)
            };
            context.Signal("user-react-conversation");
            var uow = contents.CreateUnitOfWork(key.Pk);
            if (current == null) uow.Create(updated); else uow.Update(updated);
            try { await uow.SaveChangesAsync(context); return; }
            catch (UnitOfWorkException ex) when (ContentContainer.IsConcurrencyFailure(ex)) { }
        }
    }

    public async Task DeleteConversationAsync(UserSession user, string conversationId, OperationContext context)
    {
        try
        {
            var contents = GetContentsContainer();
        
            var conversation = await contents.MarkConversationDeletedAsync(user.UserId, conversationId, context);
            if (conversation == null)
                throw new ContentException(ContentError.ContentNotFound);


        }
        catch (Exception e)
        {
            throw new ContentException(ContentError.UnexpectedError, e);
        }
    }
    
    public async Task<Conversation> GetConversationAsync(string handle, string conversationId, int lastCommentCount, OperationContext context)
    {
        var contents = GetContentsContainer();
        try
        {
            var conversationUserId = await _userHandleService.GetUserIdAsync(handle, context);
            var query = new ConversationQuery()
            {
                UserId = conversationUserId,
                ConversationId = conversationId,
                LastCommentCount = lastCommentCount,
            };
            var tuple = await _queries.QuerySingleAsync(contents, query, context);
            if(tuple?.ConversationTuple == null)
                throw new ContentException(ContentError.ContentNotFound);
            
            var view = new ConversationViewDocument(conversationUserId, conversationId, Guid.NewGuid().ToString("N"),
                tuple.ConversationTuple.Conversation.ProcessingRegion)
            {
                CreationDate = context.UtcNow.UtcDateTime
            };
            var uow = contents.CreateUnitOfWork(view.Pk);
            uow.Create(view);
            await uow.SaveChangesAsync(context);

            return await BuildConversationModelAsync(tuple.ConversationTuple.Conversation, tuple.ConversationTuple.Counts, tuple.Comments, context);
        }
        catch (CosmosException e)
        {
            throw new ContentException(ContentError.UnexpectedError, e);
        }
    }
    
    public async Task<IReadOnlyList<ConversationComment>> GetPreviousCommentsAsync(string handle, string conversationId, string? commentId, int lastCommentCount, OperationContext context)
    {
        try
        {
            var contents = GetContentsContainer();
            var conversationUserId = await _userHandleService.GetUserIdAsync(handle, context);
            var query = new PreviousCommentsQuery()
            {
                UserId = conversationUserId,
                ConversationId = conversationId,
                LastCommentCount = lastCommentCount,
                CommentId = commentId
            };
            
            var list = new ConversationComment[lastCommentCount];
            var next = lastCommentCount - 1;
            await foreach (var tuple in _queries.QueryManyAsync(contents, query, context))
            {
                var comment = await BuildCommentAsync(tuple.Comment, tuple.Counts, context);
                list[next--] = comment;
            }

            var missing = next + 1;
            return new List<ConversationComment>(list.Skip(missing));
        }
        catch (CosmosException e)
        {
            throw new ContentException(ContentError.UnexpectedError, e);
        }
    }
    
    public async Task<IReadOnlyList<ConversationRoot>> GetUserConversationsAsync(string handle, string? beforeConversationId, int limit, OperationContext context)
    {
        var contents = GetContentsContainer();

        try
        {
            var conversationUserId = await _userHandleService.GetUserIdAsync(handle, context);
            var query = new UserConversationsQuery()
            {
                UserId = conversationUserId,
                BeforeConversationId = beforeConversationId,
                Limit = limit
            };
            
            var conversationsModels = new List<ConversationRoot>(limit);
            await foreach (var tuple in _queries.QueryManyAsync(contents, query, context))
            {
                var conversation = await BuildConversationAsync(tuple.Conversation, tuple.Counts, context);
                conversationsModels.Add(conversation.Root);
            }
            
            return conversationsModels;
        }
        catch (CosmosException e)
        {
            throw new ContentException(ContentError.UnexpectedError, e);
        }
    }
    
    
    private async Task<Conversation> BuildConversationModelAsync(ConversationDocument conversation, ConversationCountsDocument conversationCounts, IReadOnlyList<CommentTuple>? comments, OperationContext context)
    {
        var model = await BuildConversationAsync(conversation, conversationCounts, context);

        
        if (comments != null)
        {
            var sorted = comments
                .OrderBy(x => x.Comment.Sk);
            
            foreach (var (commentDocument, commentCountsDocument) in sorted)
            {
                if (commentDocument.CommentId != commentCountsDocument.CommentId)
                    throw new InvalidOperationException($"The comment {commentDocument.CommentId} does not match the counts.");
                
                var comment = await BuildCommentAsync(commentDocument, commentCountsDocument, context);
                model.LastComments.Add(comment);
            }
        }

        return model;
    }
    
    private async Task<ConversationComment> BuildCommentAsync(CommentDocument comment, CommentCountsDocument counts, OperationContext context)
        => new()
        {
            Handle = await _userHandleService.GetHandleAsync(comment.UserId, context),
            CommentId = comment.CommentId,
            Content = comment.Content,
            LastModify = comment.LastModify,
            ViewCount = counts.GetViewCount(),
            CommentCount = counts.GetCommentCount(),
            LikeCount = counts.GetLikeCount()
        };

    private async Task<Conversation> BuildConversationAsync(ConversationDocument conversation, ConversationCountsDocument counts, OperationContext context)
        => new()
        {
            Root = new ConversationRoot
            {
                Handle = await _userHandleService.GetHandleAsync(conversation.UserId, context),
                ConversationId = conversation.ConversationId,
                Content = conversation.Content,
                LastModify = conversation.LastModify,
                CommentCount = counts.GetCommentCount(),
                ViewCount = counts.GetViewCount(),
                LikeCount = counts.GetLikeCount(),
            },
            LastComments = new List<ConversationComment>()
        };
}
