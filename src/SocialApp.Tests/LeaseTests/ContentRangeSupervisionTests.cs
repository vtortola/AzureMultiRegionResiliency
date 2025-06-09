using SocialApp.WebApi.Features.Content.Services;

namespace SocialApp.Tests.LeaseTests;

public class ContentRangeSupervisionTests
{
    [Test]
    public async Task FailedRange_CancelsAndAwaitsInfiniteSibling_PreservingFailure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        async Task PollAsync(CancellationToken cancel)
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancel); }
            finally { stopped = true; }
        }
        async Task FailAsync(CancellationToken cancel)
        {
            await started.Task.WaitAsync(cancel);
            throw new InvalidOperationException("range failed");
        }
        var running = ContentStreamProcessorService.RunRangesAsync([PollAsync, FailAsync], CancellationToken.None);
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(error!.Message, Is.EqualTo("range failed"));
        Assert.That(stopped, Is.True);
        await Task.CompletedTask;
    }
}
