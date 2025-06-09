namespace SocialApp.WebApi.Features._Shared.Services;

public static class Passwords
{
    /*
     * This is a dummy implementation of a password hasher.
     * I need to verify that the password is being hashed,
     * but for debugging purposes, I need to verify that the
     * password being hashed is the right string in a way that
     * can be easily spotted by the naked eye.
     * In a production environment this should be a proper
     * hashing algorithm.
     */
    public static string HashPassword(string password)
    {
        return "$$" + password + "$$";
    }
}