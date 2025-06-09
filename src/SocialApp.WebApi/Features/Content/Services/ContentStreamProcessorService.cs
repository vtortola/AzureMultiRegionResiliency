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
        var contents = GetContentContainer();
        var ranges = await contents.GetFeedRangesAsync();
        await RunRangesAsync(ranges.Select<string, Func<CancellationToken, Task>>(range =>
            cancel => ProcessRangeAsync(contents, range, lease, cancel)), lease.Cancellation);
    }

    internal static async Task RunRangesAsync(IEnumerable<Func<CancellationToken, Task>> ranges, CancellationToken cancel)
    {
        using var group = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var tasks = ranges.Select(RunAsync).ToArray();
        await Task.WhenAll(tasks);

        async Task RunAsync(Func<CancellationToken, Task> range)
        {
            try { await range(group.Token); }
            catch { await group.CancelAsync(); throw; }
        }
    }

    private async Task ProcessRangeAsync(ContentContainer contents, string range, ProcessorLease lease, CancellationToken cancel)
    {
        _logger.LogInformation("Processing content change feed range {Range} for region {ProcessingRegion}.", range, _processingRegion);
        var continuation = await GetContinuationTokenAsync(range, cancel);
        var follows = GetFollowContainer();
        await foreach (var (documents, updatedContinuation) in contents.ReadFeedAsync(range, continuation, cancel))
        {
            var regionalDocuments = documents.Where(d => d is IRegionOwnedDocument owned
                && string.Equals(owned.ProcessingRegion, _processingRegion, StringComparison.OrdinalIgnoreCase));
            await Parallel.ForEachAsync(regionalDocuments, cancel, async (document, c)
                => await ProcessDocumentAsync(contents, follows, document, c));
            cancel.ThrowIfCancellationRequested();
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
                    await contents.EnsureCountsAsync(doc, context);
                    error = StreamProcessingError.ConversationToParentComment;
                    await contents.SyncParentCommentAsync(doc, context);
                    error = StreamProcessingError.ConversationToFeed;
                    await PropagateConversationToFollowersFeedsAsync(GetFeedContainer(), follows, doc, context);
                    break;

                case ConversationCountsDocument doc:
                    error = StreamProcessingError.ConversationCountToParentComment;
                    await contents.MergeParentCommentCountsAsync(doc, context);
                    error = StreamProcessingError.ConversationCountToFeed;
                    await PropagateConversationCountsToFollowersFeedAsync(GetFeedContainer(), follows, doc, context);
                    break;

                case CommentDocument doc:
                    // Parent comments are projections, never a source for recreating conversations.
                    break;

                case ConversationUserLikeDocument doc:
                    error = StreamProcessingError.VerifyLikePropagation;
                    await contents.SetConversationLikeAsync(doc, context);
                    break;
                
                case ConversationViewDocument doc:
                    await contents.ApplyViewAsync(doc, context);
                    break;

                case ConversationLikeDocument _:
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
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
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

    private void ReportOperationCharge(Document document, OperationContext context)
    {
        if(context.OperationCharge == 0)
            return;
        
        _logger.LogInformation($"Handle({document.GetType().Name}): {context.OperationCharge}");
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
                Ttl = -1,
                Deleted = counts.Deleted,
            };
            await ContentContainer.SaveProjectionAsync(contents, feedItem, counts.ProcessingRegion, context);
        }
    }

    private async Task PropagateConversationToFollowersFeedsAsync(FeedContainer container, FollowContainer follows, ConversationDocument conversation, OperationContext context)
    {
        var followers1 = await GetFollowerListAsync(follows, conversation.UserId, context);
        foreach (var followerId in GetFollowersAsync(followers1))
        {
            var feedItem = FeedConversationDocument.From(followerId, conversation) with
            {
                Ttl = -1,
                Deleted = conversation.Deleted
            };
            
            await ContentContainer.SaveProjectionAsync(container, feedItem, conversation.ProcessingRegion, context);
        }
    }

    private static HashSet<string> GetFollowersAsync(FollowerListDocument? followers)
        => followers?.Followers??[];
}
