namespace SocialApp.WebApi.Data.User;

public interface IRegionOwnedDocument
{
    DateTime CreationDate { get; }
    string ProcessingRegion { get; }
}
