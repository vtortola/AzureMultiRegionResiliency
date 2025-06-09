using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Infrastructure.Leases;
using SocialApp.WebApi.Features.Content.Containers;

namespace SocialApp.WebApi.Features.Content.Services;

public interface IContentConflictResolutionService
{
    Task ProcessConflictResolutionFeedAsync(CancellationToken cancel);
}

public sealed class ContentConflictResolutionService : IContentConflictResolutionService
{
    private readonly UserDatabase _userDb;
    private readonly ProcessorLeaseCoordinator _leases;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContentConflictResolutionService> _logger;

    public ContentConflictResolutionService(UserDatabase userDb, IConfiguration configuration, ProcessorLeaseCoordinator leases, ILogger<ContentConflictResolutionService> logger)
    {
        _userDb = userDb;
        _leases = leases;
        _configuration = configuration;
        _logger = logger;
    }
    
    private ContentContainer GetContentContainer()
        => new(_userDb);
    
    private int WaitSecondsOnEmptyFeed
        => _configuration.GetValue("Content:ConflictResolution:WaitSecondsOnEmptyFeed", 60);

    public Task ProcessConflictResolutionFeedAsync(CancellationToken cancel)
        => _leases.RunAsync("ContentConflictProcessor", _userDb.Region,
            lease => ProcessOwnedConflictFeedAsync(lease.Cancellation), cancel);

    private async Task ProcessOwnedConflictFeedAsync(CancellationToken cancel)
    {
        _logger.LogInformation("Processing content conflict resolution feed...");
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var contents = GetContentContainer();
                var merger = new ContentConflictMerger(contents, _logger);
                var count = 0;
                await foreach (var resolution in contents.ProcessConflictFeedAsync(merger, cancel))
                {
                    count++;
                    if (resolution.Merged)
                        _logger.LogInformation("Processed content conflict {pk}/{id}.", resolution.Key.Pk, resolution.Key.Id);
                    else
                        _logger.LogWarning("Unable to process content conflict {pk}/{id}.", resolution.Key.Pk, resolution.Key.Id);
                }

                if (count == 0)
                    await Task.Delay(WaitSecondsOnEmptyFeed * 1000, cancel);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing content change feed.");
                await Task.Delay(WaitSecondsOnEmptyFeed * 1000, cancel);
            }
        }
    }
}