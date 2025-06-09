using SocialApp.WebApi.Data._Shared;

namespace SocialApp.WebApi.Data.Account;

public record ProcessorLeaseDocument(string Processor, string Region, string OwnerId, string AcquisitionId, DateTimeOffset ExpiresAt)
    : Document(Key(Processor, Region))
{
    public static DocumentKey Key(string processor, string region)
        => new(ChangeFeedProgressDocument.Key(processor, region, string.Empty).Pk, "processor-lease");
}
