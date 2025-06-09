using Microsoft.Azure.Cosmos;
using SocialApp.WebApi.Infrastructure.Leases;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Account.Containers;
using SocialApp.WebApi.Features.Content.Containers;
using SocialApp.WebApi.Features.Content.Exceptions;
using SocialApp.WebApi.Features.Follow.Containers;

namespace SocialApp.WebApi.Features.Content.Services;

public interface IContentStreamProcessorService
{
    Task ProcessChangeFeedAsync(CancellationToken cancel);
}

public sealed class ContentStreamProcessorService : IContentStreamProcessorService
{
    private readonly UserDatabase _userDb;
    private readonly ProcessorLeaseCoordinator _leases;
    private readonly AccountContainer _checkpoints;
    private readonly string _processingRegion;
    private readonly ILogger<ContentStreamProcessorService> _logger;

    private  int FeedItemTtl => (int)TimeSpan.FromDays(2).TotalSeconds;
    
    public ContentStreamProcessorService(UserDatabase userDb, AccountDatabase accountDb, IConfiguration configuration, ProcessorLeaseCoordinator leases, ILogger<ContentStreamProcessorService> logger)
    {
        _userDb = userDb;
        _leases = leases;
        _checkpoints = new AccountContainer(accountDb);
        var processingRegion = configuration["Content:ProcessingRegion"];
        _processingRegion = string.IsNullOrWhiteSpace(processingRegion)
            ? userDb.Region
            : processingRegion.Trim().ToLowerInvariant();
        _logger = logger;
    }

    private FeedContainer GetFeedContainer()
        => new(_userDb);
    
    private FollowContainer GetFollowContainer()
        => new(_userDb);
    
    private ContentContainer GetContentContainer()
        => new(_userDb);
    
    public Task ProcessChangeFeedAsync(CancellationToken cancel)
        => _leases.RunAsync("ContentFeedProcessor", _processingRegion, ProcessOwnedFeedAsync, cancel);

    private async Task ProcessOwnedFeedAsync(ProcessorLease lease)
    {
        var cancel = lease.Cancellation;
        var contents = GetContentContainer();
        var ranges = await contents.GetFeedRangesAsync();
        var tasks = new List<Task>();
        
        foreach (var range in ranges)
            tasks.Add(Task.Run(() => ProcessRangeAsync(contents, range, lease), cancel));
        
        await Task.WhenAll(tasks);
    }

    private async Task ProcessRangeAsync(ContentContainer contents, string range, ProcessorLease lease)
    {
        var cancel = lease.Cancellation;
        _logger.LogInformation("Processing content change feed range {Range} for region {ProcessingRegion}.", range, _processingRegion);

        var continuation = await GetContinuationTokenAsync(range, cancel);
        
        var follows = GetFollowContainer();
        await foreach (var (documents, updatedContinuation) in contents.ReadFeedAsync(range, continuation, cancel))
        {
            var regionalDocuments = documents.Where(d => d is IRegionOwnedDocument owned
                && string.Equals(owned.ProcessingRegion, _processingRegion, StringComparison.OrdinalIgnoreCase));
            await Parallel.ForEachAsync(regionalDocuments, cancel, async (document, c)
                => await ProcessDocumentAsync(contents, follows, document, c));
            
            // Every region reads the entire feed and advances independently, even over skipped items.
            // Saving progress also checks that this acquisition still owns the regional lease.
            await lease.SaveCheckpointAsync(new ChangeFeedProgressDocument("ContentFeedProcessor", _processingRegion, range, updatedContinuation));
        }
    }

    private async Task<string?> GetContinuationTokenAsync(string range, CancellationToken cancel)
    {
        var key = ChangeFeedProgressDocument.Key("ContentFeedProcessor", _processingRegion, range);
        var document = await _checkpoints.GetAsync<ChangeFeedProgressDocument>(key, new OperationContext(cancel));
        return document?.Continuation;
    }
    
    private async Task ProcessDocumentAsync(ContentContainer contents, FollowContainer follows, Document document, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var context = new OperationContext(cancel);
        var error = StreamProcessingError.UnexpectedError;
        try
        {
            switch (document)
            {
                case ConversationDocument doc:
                    error = StreamProcessingError.ConversationToParentComment;
                    await SyncConversationToParentCommentAsync(contents, doc, context);
                    error = StreamProcessingError.ConversationToFeed;
                    await PropagateConversationToFollowersFeedsAsync(GetFeedContainer(), follows, doc, context);
                    break;

                case ConversationCountsDocument doc:
                    error = StreamProcessingError.ConversationCountToParentComment;
                    await SyncConversationCountsToParentCommentAsync(contents, doc, context);
                    error = StreamProcessingError.ConversationCountToFeed;
                    await PropagateConversationCountsToFollowersFeedAsync(GetFeedContainer(), follows, doc, context);
                    break;

                case CommentDocument doc:
                    error = StreamProcessingError.VerifyChildConversationCreation;
                    await EnsureChildConversationIsCreatedOnCommentAsync(contents, doc, context);
                    break;

                case ConversationUserLikeDocument doc:
                    error = StreamProcessingError.VerifyLikePropagation;
                    await EnsureLikeHasPropagatedAsync(contents, doc, context);
                    break;
                
                case CommentLikeDocument _:
                    // Do nothing. 
                    break;
                        
                case CommentCountsDocument _:
                    // Do nothing.
                    break;
                        
                default:
                    // Document should not be in container.
                    _logger.LogWarning("Content processor processing unexpected document: '{fullName}'", document.GetType().FullName);
                    break;
            }
        }
        catch (CosmosException e)
        {
            context.AddRequestCharge(e.RequestCharge);
            throw new StreamProcessingException(error, document.Pk, document.Id, e);
        }
        catch (Exception e)
        {
            throw new StreamProcessingException(error, document.Pk, document.Id, e);
        }
        finally
        {
            ReportOperationCharge(document, context);
        }
    }

    private async Task EnsureLikeHasPropagatedAsync(ContentContainer contents, ConversationUserLikeDocument doc, OperationContext context)
    {
        var key = ConversationLikeDocument.Key(doc.ConversationUserId, doc.ConversationId, doc.UserId);
        var conversationLike = await contents.GetAsync<ConversationLikeDocument>(key, context);
        
        if (conversationLike == null || conversationLike.Like != doc.Like)
        {
            conversationLike = new ConversationLikeDocument(doc.ConversationUserId, doc.ConversationId, doc.UserId, doc.Like)
            {
                Ttl = doc.Like ? -1 : (int)TimeSpan.FromDays(2).TotalSeconds
            };

            var countKey = ConversationCountsDocument.Key(conversationLike.ConversationUserId, conversationLike.ConversationId);
                
            var uow = contents.CreateUnitOfWork(conversationLike.Pk);
            uow.CreateOrUpdate(conversationLike);
            uow.IncrementConversationLikes(countKey, conversationLike.Like ? 1 : -1);
            await uow.SaveChangesAsync(context);
        }
        
        if(string.IsNullOrWhiteSpace(doc.ParentConversationUserId))
            return;
        if (string.IsNullOrWhiteSpace(doc.ParentConversationId))
            throw new InvalidOperationException("Missing parent conversation ID.");

        var commentLikeKey = CommentLikeDocument.Key(doc.ParentConversationUserId, doc.ParentConversationId, doc.ConversationId, doc.UserId);
        var commentLike = await contents.GetAsync<CommentLikeDocument>(commentLikeKey, context);

        if (commentLike == null || commentLike.Like != doc.Like)
        {
            commentLike = new CommentLikeDocument(doc.ParentConversationUserId, doc.ParentConversationId, doc.ConversationId, doc.UserId, doc.Like)
            {
                Ttl = doc.Like ? -1 : (int)TimeSpan.FromDays(2).TotalSeconds
            }; 
            var countKey = CommentCountsDocument.Key(commentLike.ConversationUserId, commentLike.ConversationId, commentLike.CommentId);
                    
            var uow = contents.CreateUnitOfWork(commentLike.Pk);
            uow.CreateOrUpdate(commentLike);
            uow.IncrementCommentLikes(countKey, commentLike.Like ? 1 : -1);
            await uow.SaveChangesAsync(context);
        }
    } 

    private void ReportOperationCharge(Document document, OperationContext context)
    {
        if(context.OperationCharge == 0)
            return;
        
        _logger.LogInformation($"Handle({document.GetType().Name}): {context.OperationCharge}");
    }

    private async Task SyncConversationToParentCommentAsync(ContentContainer contents, ConversationDocument conversation, OperationContext context)
    {
        if(conversation.IsRootConversation)
            return; // It has no parent comment
        if (string.IsNullOrWhiteSpace(conversation.ParentConversationUserId)
            || string.IsNullOrWhiteSpace(conversation.ParentConversationId))
            throw new InvalidOperationException("A comment conversation must identify its parent conversation and user.");
        
        var key = CommentDocument.Key(conversation.ParentConversationUserId, conversation.ParentConversationId, conversation.ConversationId);
        var comment = await contents.GetAsync<CommentDocument>(key, context);
        if(comment == null)
        {
            if(conversation.Deleted)
                return;
            
            // Comment was not created
            comment = new CommentDocument(conversation.ParentConversationUserId, conversation.ParentConversationId, conversation.UserId, conversation.ConversationId, conversation.Content, conversation.LastModify, conversation.Version, conversation.ProcessingRegion);
            var commentConversationKey = ConversationCountsDocument.Key(comment.ConversationUserId, comment.ConversationId);
            
            context.Signal("create-comment");
            var uow = contents.CreateUnitOfWork(comment.Pk);
            uow.Create(comment);
            uow.Create(comment.CreateCounts());
            uow.IncrementConversationComments(commentConversationKey);
            await uow.SaveChangesAsync(context);
        }
        else if (conversation.Deleted && !comment.Deleted)
        {
            // Delete comment and counts
            var commentKey = CommentDocument.Key(comment.ConversationUserId, comment.ConversationId, comment.CommentId);
                    
            var uow = contents.CreateUnitOfWork(commentKey.Pk);
            uow.Set<CommentDocument>(key, c => c.Deleted, true );
            uow.Set<CommentDocument>(key, c => c.Ttl, TimeSpan.FromDays(1).TotalSeconds );
            uow.Set<CommentCountsDocument>(key, c => c.Deleted, true );
            uow.Set<CommentCountsDocument>(key, c => c.Ttl, TimeSpan.FromDays(1).TotalSeconds );
            var parentKey = ConversationCountsDocument.Key(conversation.ParentConversationUserId, conversation.ParentConversationId);
            uow.IncrementConversationComments(parentKey, -1);
            await uow.SaveChangesAsync(context);
        }
        else if (comment.Version < conversation.Version)
        {
            // Comment is outdated
            var uow = contents.CreateUnitOfWork(comment.Pk);
            uow.Update(comment with
            {
                Content = conversation.Content, 
                Version = conversation.Version
            });
            await uow.SaveChangesAsync(context);
        }
    }
    
    private async Task SyncConversationCountsToParentCommentAsync(ContentContainer contents, ConversationCountsDocument tcounts, OperationContext context)
    {
        if(tcounts.IsRootConversation)
            return; // It has no parent comment
        
        if(!tcounts.NoCounterIsZero() && !tcounts.Deleted)
            return;

        var ccounts = CommentCountsDocument.TryGenerateParentCommentCounts(tcounts);
        if(ccounts == null)
            return;
        
        var uow = contents.CreateUnitOfWork(ccounts.Pk);
        uow.Update(ccounts);
        await uow.SaveChangesAsync(context);
    }

    private async Task EnsureChildConversationIsCreatedOnCommentAsync(ContentContainer contents, CommentDocument comment, OperationContext context)
    {
        var key = ConversationDocument.Key(comment.UserId, comment.CommentId);
        var conversation = await contents.GetAsync<ConversationDocument>(key, context);
        if(conversation != null)
            return;

        conversation = new ConversationDocument(comment.UserId, comment.CommentId, comment.Content, comment.LastModify, comment.Version, comment.ConversationUserId, comment.ConversationId, comment.ProcessingRegion);
        try
        {
            var uow = contents.CreateUnitOfWork(conversation.Pk);
            uow.Create(conversation);
            uow.Create(conversation.CreateCounts());
            await uow.SaveChangesAsync(context);
        }
        catch (UnitOfWorkException e) when (e.Error == OperationError.Conflict)
        {
            // This can happen if the Change Feed is catching up with the main conversation
        }
    }

    private static async Task<FollowerListDocument?> GetFollowerListAsync(FollowContainer container, string userId, OperationContext context)
    {
        var followerKey = FollowerListDocument.Key(userId);
        var followerList = await container.GetAsync<FollowerListDocument>(followerKey, context);
        return followerList;
    }


    private async Task PropagateConversationCountsToFollowersFeedAsync(FeedContainer contents, FollowContainer follows, ConversationCountsDocument counts, OperationContext context)
    {
        var followers2 = await GetFollowerListAsync(follows, counts.UserId, context);
        foreach (var followerId in GetFollowersAsync(followers2))
        {
            var feedItem = FeedConversationCountsDocument.From(followerId, counts) with
            {
                Ttl = FeedItemTtl,
                Deleted = counts.Deleted,
            };
            var uow = contents.CreateUnitOfWork(feedItem.Pk);
            uow.CreateOrUpdate(feedItem);
            await uow.SaveChangesAsync(context);
        }
    }

    private async Task PropagateConversationToFollowersFeedsAsync(FeedContainer container, FollowContainer follows, ConversationDocument conversation, OperationContext context)
    {
        var followers1 = await GetFollowerListAsync(follows, conversation.UserId, context);
        foreach (var followerId in GetFollowersAsync(followers1))
        {
            var feedItem = FeedConversationDocument.From(followerId, conversation) with
            {
                Ttl = FeedItemTtl,
                Deleted = conversation.Deleted
            };
            
            var uow = container.CreateUnitOfWork(feedItem.Pk);
            uow.CreateOrUpdate(feedItem);
            await uow.SaveChangesAsync(context);
        }
    }

    private static HashSet<string> GetFollowersAsync(FollowerListDocument? followers)
        => followers?.Followers??[];
}
