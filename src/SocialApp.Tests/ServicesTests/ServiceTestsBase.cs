using System.Net;
using SocialApp.WebApi.Infrastructure.Leases;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Data.Session;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Account;
using SocialApp.WebApi.Features.Account.Containers;
using SocialApp.WebApi.Features.Account.Services;
using SocialApp.WebApi.Features.Content;
using SocialApp.WebApi.Features.Content.Containers;
using SocialApp.WebApi.Features.Content.Services;
using SocialApp.WebApi.Features.Follow.Services;
using SocialApp.WebApi.Features.Session.Models;
using SocialApp.WebApi.Features.Session.Services;

namespace SocialApp.Tests.ServicesTests;

public abstract class ServiceTestsBase
{
    protected bool RemoveContainerAfterTests = true;
    
    private readonly IConfiguration _configuration;
    
    protected AccountService AccountService;
    protected SessionService SessionService;
    protected FollowersService FollowersService;
    protected ContentService ContentService;
    protected FeedService FeedService;
    protected ContentStreamProcessorService ContentStreamProcessorService;
    protected ContentConflictResolutionService ContentConflictResolutionService;
    
    private AccountDatabase _accountDatabase;
    protected UserDatabase UserDatabase;
    private SessionDatabase _sessionDatabase;
    
    private CosmosClient _cosmosClient;
    private Task _changeFeedTask = Task.CompletedTask;
    
    private readonly CancellationTokenSource _changeFeedCancellationToken = new CancellationTokenSource();
    
    public Exception? FeedError { get; private set; }
    public Exception? ConflictFeedError { get; private set; }
    
    protected ServiceTestsBase()
    {
        _configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();
    }
    
    protected static OperationContext CreateContextWithSession(OperationContext? context)
    {
        var newcontext = OperationContext.New();
        newcontext.SessionTokens = context?.SessionTokens;
        return newcontext;
    }
    
    [OneTimeSetUp]
    public async Task Setup()
    {
        _cosmosClient = CosmoDatabase.CreateCosmosClient
        (
            _configuration.GetSection("CosmosDb:User"),
            _configuration.GetValue<string>("CosmosDb:ApplicationName") ?? throw new SocialAppConfigurationException("Missing CosmosDb ApplicationName")
        );
        
        var loggerFactory = new NullLoggerFactory();
        
        _accountDatabase = new AccountDatabase(_cosmosClient, _configuration.GetSection("CosmosDb:Account"));
        UserDatabase = new UserDatabase(_cosmosClient, _configuration.GetSection("CosmosDb:User"));
        _sessionDatabase = new SessionDatabase(_cosmosClient, _configuration.GetSection("CosmosDb:Session"));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IQueries>(s => new QueryResolver(s));
        serviceCollection.AddSingleton(_accountDatabase);
        serviceCollection.AddSingleton(UserDatabase);
        serviceCollection.AddSingleton(_sessionDatabase);
        serviceCollection.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        serviceCollection.RegisterAccountServices();
        serviceCollection.RegisterContentServices();

        var services = serviceCollection.BuildServiceProvider();
        var queries = services.GetRequiredService<IQueries>();
        
        AccountService = new AccountService(_accountDatabase, UserDatabase, new Logger<AccountService>(loggerFactory), queries);
        SessionService = new SessionService(UserDatabase, _sessionDatabase, queries);
        FollowersService = new FollowersService(UserDatabase, services.GetRequiredService<IUserHandleService>());
        ContentService = new ContentService(UserDatabase,  services.GetRequiredService<IUserHandleService>(), queries);
        FeedService = new FeedService(UserDatabase,  services.GetRequiredService<IUserHandleService>(), queries);
       
        var leases = new ProcessorLeaseCoordinator(new ProcessorLeaseStore(_accountDatabase, NullLogger<ProcessorLeaseStore>.Instance), TimeProvider.System, NullLogger<ProcessorLeaseCoordinator>.Instance);
        ContentStreamProcessorService = new ContentStreamProcessorService(UserDatabase, _accountDatabase, _configuration, leases, new Logger<ContentStreamProcessorService>(loggerFactory));
        ContentConflictResolutionService = new ContentConflictResolutionService(UserDatabase, _configuration, leases, new Logger<ContentConflictResolutionService>(loggerFactory));
        
        await UserDatabase.InitializeAsync();
        
        _changeFeedTask = Task.Run(ProcessChangeFeedAsync);
    }

    private async Task ProcessChangeFeedAsync()
    {
        try
        {
            await ContentStreamProcessorService.ProcessChangeFeedAsync(_changeFeedCancellationToken.Token);
        }
        catch (OperationCanceledException) when (_changeFeedCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            FeedError = e;
        }
    }
    
    private async Task ProcessConflictFeedAsync()
    {
        try
        {
            await ContentConflictResolutionService.ProcessConflictResolutionFeedAsync(_changeFeedCancellationToken.Token);
        }
        catch (Exception e)
        {
            ConflictFeedError = e;
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _changeFeedCancellationToken.CancelAsync();
        await _changeFeedTask.WaitAsync(TimeSpan.FromSeconds(10));
        var feedError = FeedError;
        var conflictFeedError = ConflictFeedError;
        _changeFeedCancellationToken.Dispose();
        if(RemoveContainerAfterTests)
        {
            // Account checkpoints outlive the content container unless we remove our test progress too.
            var checkpoints = new AccountContainer(_accountDatabase);
            var configuredRegion = _configuration["Content:ProcessingRegion"];
            var region = string.IsNullOrWhiteSpace(configuredRegion)
                ? UserDatabase.Region : configuredRegion.Trim().ToLowerInvariant();
            foreach (var range in await new ContentContainer(UserDatabase).GetFeedRangesAsync())
            {
                var key = ChangeFeedProgressDocument.Key("ContentFeedProcessor", region, range);
                var checkpoint = await checkpoints.GetAsync<ChangeFeedProgressDocument>(key, OperationContext.New());
                if (checkpoint == null)
                    continue;
                var uow = checkpoints.CreateUnitOfWork(key.Pk);
                uow.Delete(checkpoint);
                await uow.SaveChangesAsync(OperationContext.New());
            }
            await _cosmosClient.GetDatabase(_configuration.GetValue<string>("CosmosDb:User:Id")).GetContainer(_configuration.GetValue<string>("CosmosDb:User:Containers:Contents:Id")).DeleteContainerAsync();
        }
        _cosmosClient.Dispose();
        if(feedError != null)
            throw feedError;
        if(conflictFeedError != null)
            throw conflictFeedError;
    }
    
    protected async Task<UserSession> CreateUserAsync(string username = "")
    {
        var userName = username + Guid.NewGuid().ToString("N");
        await AccountService.RegisterAsync(new ($"{userName}@xxx.com", userName, "Display"+userName, "pass"), OperationContext.New());
        var session = await SessionService.LoginWithPasswordAsync(new($"{userName}@xxx.com", "pass"), OperationContext.New());
        return session ?? throw new InvalidOperationException("Cannot find user");
    }
    
    protected static CosmosException CreateCosmoException(HttpStatusCode code = HttpStatusCode.Unused)
        => new(code.ToString(), code, (int)code, Guid.NewGuid().ToString(), 2);
}
