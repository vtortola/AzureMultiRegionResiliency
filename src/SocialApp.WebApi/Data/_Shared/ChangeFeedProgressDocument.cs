using System.Text;

namespace SocialApp.WebApi.Data._Shared;

public record ChangeFeedProgressDocument(string Processor, string Region, string Range, string? Continuation)
    : Document(Key(Processor, Region, Range))
{
    public static DocumentKey Key(string processor, string region, string range)
    {
        var pk = "change_feed_progress:"+processor+":"+region;
        var id = Convert.ToBase64String(Encoding.UTF8.GetBytes(range));
        return new DocumentKey(pk, id);
    }
}