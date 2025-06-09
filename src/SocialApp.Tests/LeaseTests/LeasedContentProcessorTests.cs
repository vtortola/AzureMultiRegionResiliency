using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Features.Account.Containers;
using SocialApp.WebApi.Features.Content.Containers;
using SocialApp.WebApi.Features.Content.Services;
using SocialApp.WebApi.Infrastructure.Leases;

namespace SocialApp.Tests.LeaseTests;

public class LeasedContentProcessorTests
{
    [Test]
    public async Task Replacement_ResumesCheckpoint_AndKeepsRegionalFiltering()
    {
        var settings = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddJsonFile("appsettings.Local.json", true).Build();
        using var users = CosmoDatabase.CreateCosmosClient(settings.GetSection("CosmosDb:User"), "LeasedContentTests");
        using var accounts = CosmoDatabase.CreateCosmosClient(settings.GetSection("CosmosDb:Account"), "LeasedContentTests");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancel = timeout.Token;
        var suffix = Guid.NewGuid().ToString("N");
        var content = (await users.GetDatabase(settings["CosmosDb:User:Id"]!).CreateContainerAsync(
            new ContainerProperties("lease-content-" + suffix, "/pk"), cancellationToken: cancel)).Container;
        Container? progress = null;
        try
        {
            progress = (await accounts.GetDatabase(settings["CosmosDb:Account:Id"]!).CreateContainerAsync(
                new ContainerProperties("lease-progress-" + suffix, "/pk"), cancellationToken: cancel)).Container;
            var userDb = new UserDatabase(users, Configuration(new()
            {
                ["Id"] = settings["CosmosDb:User:Id"], ["Containers:contents:Id"] = content.Id,
                ["Containers:follows:Id"] = content.Id, ["Containers:feeds:Id"] = content.Id
            }));
            var accountDb = new AccountDatabase(accounts, Configuration(new()
            {
                ["Id"] = settings["CosmosDb:Account:Id"], ["Containers:accounts:Id"] = progress.Id
            }));
            var contents = new ContentContainer(userDb);
            var checkpoints = new AccountContainer(accountDb);
            var range = (await contents.GetFeedRangesAsync()).Single();
            var first = Comment("first", "eastus2");
            var foreign = Comment("foreign", "westus2");
            await content.CreateItemAsync(first, new PartitionKey(first.Pk), cancellationToken: cancel);
            await content.CreateItemAsync(foreign, new PartitionKey(foreign.Pk), cancellationToken: cancel);
            await RunAsync(first, foreign);
            Assert.That(await ChildAsync(foreign), Is.Null);
            var key = ConversationDocument.Key(first.UserId, first.CommentId);
            await content.DeleteItemAsync<ConversationDocument>(key.Id, new PartitionKey(key.Pk), cancellationToken: cancel);
            key = ConversationCountsDocument.Key(first.UserId, first.CommentId);
            await content.DeleteItemAsync<ConversationCountsDocument>(key.Id, new PartitionKey(key.Pk), cancellationToken: cancel);
            var later = Comment("later", "eastus2");
            await content.CreateItemAsync(later, new PartitionKey(later.Pk), cancellationToken: cancel);
            await RunAsync(later);
            Assert.That(await ChildAsync(first), Is.Null, "The replacement must not replay content before the saved checkpoint.");
            Assert.That(await ChildAsync(foreign), Is.Null);
            Assert.That((await ChildAsync(later))!.ProcessingRegion, Is.EqualTo("eastus2"));

            Task<ConversationDocument?> ChildAsync(CommentDocument comment)
                => contents.GetAsync<ConversationDocument>(ConversationDocument.Key(comment.UserId, comment.CommentId), new(cancel));

            async Task RunAsync(params CommentDocument[] sources)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                var coordinator = new ProcessorLeaseCoordinator(new ProcessorLeaseStore(accountDb, NullLogger<ProcessorLeaseStore>.Instance), TimeProvider.System,
                    NullLogger<ProcessorLeaseCoordinator>.Instance);
                var service = new ContentStreamProcessorService(userDb, accountDb,
                    Configuration(new() { ["Content:ProcessingRegion"] = "eastus2" }), coordinator,
                    NullLogger<ContentStreamProcessorService>.Instance);
                var running = service.ProcessChangeFeedAsync(stop.Token);
                try
                {
                    while (true)
                    {
                        if (running.IsCompleted)
                            await running;
                        var checkpoint = await checkpoints.GetAsync<ChangeFeedProgressDocument>(
                            ChangeFeedProgressDocument.Key("ContentFeedProcessor", "eastus2", range), new(cancel));
                        if (checkpoint?.Continuation != null && await ChildAsync(sources[0]) != null)
                        {
                            var passed = true;
                            using var iterator = content.GetChangeFeedIterator<JsonElement>(
                                ChangeFeedStartFrom.ContinuationToken(checkpoint.Continuation), ChangeFeedMode.LatestVersion);
                            while (iterator.HasMoreResults)
                            {
                                var page = await iterator.ReadNextAsync(cancel);
                                if (page.StatusCode == HttpStatusCode.NotModified)
                                    break;
                                if (page.Any(item => sources.Any(source => source.Id == item.GetProperty("id").GetString())))
                                    passed = false;
                            }
                            if (passed)
                                break;
                        }
                        await Task.Delay(100, cancel);
                    }
                }
                finally
                {
                    stop.Cancel();
                    try
                    {
                        await running.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (progress != null)
                    await progress.DeleteContainerAsync();
            }
            finally
            {
                await content.DeleteContainerAsync();
            }
        }
    }

    private static CommentDocument Comment(string id, string region)
        => new("parent", "conversation", "author", id, id, DateTime.UtcNow, 1, region);

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
