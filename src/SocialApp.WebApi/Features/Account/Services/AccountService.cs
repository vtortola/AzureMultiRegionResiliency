using Microsoft.Azure.Cosmos;
using SocialApp.Models.Account;
using SocialApp.WebApi.Data._Shared;
using SocialApp.WebApi.Data.Account;
using SocialApp.WebApi.Data.User;
using SocialApp.WebApi.Features._Shared.Services;
using SocialApp.WebApi.Features.Account.Containers;
using SocialApp.WebApi.Features.Account.Exceptions;
using SocialApp.WebApi.Features.Account.Queries;

namespace SocialApp.WebApi.Features.Account.Services;

public interface IAccountService
{
    Task<string> RegisterAsync(RegisterRequest request, OperationContext context);
    Task<int> RemovedExpiredPendingAccountsAsync(TimeSpan timeLimit, OperationContext context);
}

public class AccountService : IAccountService
{
    private readonly IQueries _queries;
    private readonly AccountDatabase _accountDb;
    private readonly UserDatabase _userDb;
    private readonly ILogger<AccountService> _logger;

    public AccountService(AccountDatabase accountDb, UserDatabase userDb, ILogger<AccountService> logger, IQueries queries)
    {
        _accountDb = accountDb;
        _userDb = userDb;
        _logger = logger;
        _queries = queries;
    }
    
    private ProfileContainer GetProfileContainer()
        => new(_userDb);
    
    private AccountContainer GetAccountContainer()
        => new(_accountDb);
    
    public async Task<string> RegisterAsync(RegisterRequest request, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request.Email);
        ArgumentNullException.ThrowIfNull(request.Handle);
        ArgumentNullException.ThrowIfNull(request.DisplayName);
        ArgumentNullException.ThrowIfNull(request.Password);

        var accounts = GetAccountContainer();
        
        var userId = request.DisplayName.ToLowerInvariant() +"_"+ Guid.NewGuid().ToString("N");
        var pending = new PendingAccountDocument(Ulid.NewUlid().ToString(), request.Email, userId, request.Handle, DateTime.UtcNow);
        try
        {
            context.Signal("pending-account");
            var uow = accounts.CreateUnitOfWork(pending.Pk);
            uow.Create(pending);
            await uow.SaveChangesAsync(context);

            await RegisterAccountInternalAsync(accounts, request.DisplayName, request.Password, pending, context);

            context.SuppressCancellation();
            context.Signal("complete-pending-account");
        }
        catch (AccountException) { throw; }
        catch (Exception ex)
        {
            throw new AccountException(AccountError.UnexpectedError, ex);
        }
        
        try
        {
            var uow = accounts.CreateUnitOfWork(pending.Pk);
            uow.Delete(pending);
            await uow.SaveChangesAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to delete pending account.");
        }

        return userId;
    }
    
    private async Task RegisterAccountInternalAsync(AccountContainer accounts, string displayName, string password, PendingAccountDocument pendingUserAccount, OperationContext context)
    {
        var userId = pendingUserAccount.UserId;
        var email = pendingUserAccount.Email;
        var handle = pendingUserAccount.Handle;

        var emailLock = new EmailLockDocument(email, userId);
        context.Signal("email-lock");
        
        if(!await TryCreateIfNotExistsAsync(accounts, emailLock, context))
            throw new AccountException(AccountError.EmailAlreadyRegistered);
        
        
        var handleLock = new HandleLockDocument(handle, userId);
        context.Signal("handle-lock");
        if(!await TryCreateIfNotExistsAsync(accounts, handleLock, context))
            throw new AccountException(AccountError.HandleAlreadyRegistered);

        try
        {
            var profiles = GetProfileContainer();
            
            context.Signal("create-handle");
            var handleDocument = new HandleDocument(handle, userId);
            var uow = profiles.CreateUnitOfWork(handleDocument.Pk);
            uow.Create(handleDocument);
            await uow.SaveChangesAsync(context);
            
            context.Signal("create-login");
            var passwordLogin = new PasswordLoginDocument(userId, email, Passwords.HashPassword(password));
            uow = profiles.CreateUnitOfWork(passwordLogin.Pk);
            uow.Create(passwordLogin);
            await uow.SaveChangesAsync(context);
            
            context.Signal("create-profile");
            var profile = new ProfileDocument(userId, displayName, email, handle);
            uow = profiles.CreateUnitOfWork(profile.Pk);
            uow.Create(profile);
            await uow.SaveChangesAsync(context);
        }
        catch (CosmosException e)
        {
            throw new AccountException(AccountError.UnexpectedError, e);
        }
    }
    
    private async Task<bool> TryCreateIfNotExistsAsync<T>(CosmoContainer container, T document, OperationContext context)
        where T:Document
    {
        try
        {
            var uow = container.CreateUnitOfWork(document.Pk);
            uow.Create(document);
            await uow.SaveChangesAsync(context);
            return true;
        }
        catch (UnitOfWorkException e) when ( e.Error == OperationError.Conflict)
        {
            return false;
        }
    }

    public async Task<int> RemovedExpiredPendingAccountsAsync(TimeSpan timeLimit, OperationContext context)
    {
        var accounts = GetAccountContainer();
        var profiles = GetProfileContainer();
        var pendingCount = 0;

        var query = new ExpiredPendingAccountsQueryMany(){ Limit = timeLimit };
        await foreach (var pending in _queries.QueryManyAsync(accounts, query, context))
        {
            try
            {
                var profile = await _queries.QuerySingleAsync(profiles, new ProfileQuery() { UserId = pending.UserId }, context);
                if (profile == null)
                {
                    if (await DeletePendingDataAsync(profiles, pending, context))
                    {
                        if(await TryDeleteAccountLocksAsync(accounts, pending, context))
                        {
                            var uow = accounts.CreateUnitOfWork(pending.Pk);
                            uow.Delete(pending);
                            await uow.SaveChangesAsync(context);
                            pendingCount++;
                        }
                    }
                }
                else // The profile document exists, therefore the account was created
                { 
                    var uow = accounts.CreateUnitOfWork(pending.Pk);
                    uow.Delete(pending);
                    await uow.SaveChangesAsync(context);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to remove pending account.");
            }
        }
        
        return pendingCount;
    }

    private static async Task<bool> DeletePendingDataAsync(ProfileContainer profiles, PendingAccountDocument pending, OperationContext context)
    {
        var success = true;

        try
        {
            var profileKey = ProfileDocument.Key(pending.UserId);
            var uow = profiles.CreateUnitOfWork(profileKey.Pk);
            uow.Delete<ProfileDocument>(profileKey);
            await uow.SaveChangesAsync(context);
        }
        catch (UnitOfWorkException ex) when (ex.Error == OperationError.NotFound)
        {
            
        }
        catch (Exception)
        {
            success = false;
        }
        
        success &= await TryDeleteOwnedDocumentAsync<PasswordLoginDocument>(
            profiles, PasswordLoginDocument.Key(pending.Email), pending.UserId, document => document.UserId, context);
        success &= await TryDeleteOwnedDocumentAsync<HandleDocument>(
            profiles, HandleDocument.Key(pending.Handle), pending.UserId, document => document.UserId, context);
        return success;
    }

    private static async Task<bool> TryDeleteAccountLocksAsync(AccountContainer accounts, PendingAccountDocument pending, OperationContext context)
    {
        var success = await TryDeleteOwnedDocumentAsync<EmailLockDocument>(
            accounts, EmailLockDocument.Key(pending.Email), pending.UserId, document => document.UserId, context);
        success &= await TryDeleteOwnedDocumentAsync<HandleLockDocument>(
            accounts, HandleLockDocument.Key(pending.Handle), pending.UserId, document => document.UserId, context);
        return success;
    }

    private static async Task<bool> TryDeleteOwnedDocumentAsync<T>(CosmoContainer container, DocumentKey key,
        string userId, Func<T, string> getUserId, OperationContext context)
        where T : Document
    {
        try
        {
            var document = await container.GetAsync<T>(key, context);
            if (document == null || getUserId(document) != userId)
                return true;

            if (string.IsNullOrWhiteSpace(document.ETag))
                return false;

            var uow = container.CreateUnitOfWork(key.Pk);
            uow.Delete(document);
            await uow.SaveChangesAsync(context);
            return true;
        }
        catch (UnitOfWorkException ex) when (ex.Error == OperationError.NotFound)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
