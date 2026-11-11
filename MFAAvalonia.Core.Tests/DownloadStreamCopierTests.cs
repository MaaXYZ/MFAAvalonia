using MFAAvalonia.Helper;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class DownloadStreamCopierTests
{
    [Theory]
    [InlineData(5L)]
    [InlineData(null)]
    public async Task CopiesCompleteContent(long? length)
    {
        using var source = new MemoryStream([1, 2, 3, 4, 5]);
        using var destination = new MemoryStream();
        long progress = 0;
        await DownloadStreamCopier.CopyAsync(source, destination, length, TimeSpan.FromSeconds(1), n => progress = n, TestContext.Current.CancellationToken);
        Assert.Equal(source.ToArray(), destination.ToArray());
        Assert.Equal(5, progress);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, 0L)]
    [InlineData(3, 5L)]
    [InlineData(5, 3L)]
    public async Task RejectsEmptyOrIncompleteContent(int actual, long? expected)
    {
        using var source = new MemoryStream(new byte[actual]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => DownloadStreamCopier.CopyAsync(
            source, destination, expected, TimeSpan.FromSeconds(1), _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StalledReadTimesOut()
    {
        using var source = new StalledStream();
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<TimeoutException>(() => DownloadStreamCopier.CopyAsync(
            source, destination, null, TimeSpan.FromMilliseconds(50), _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new StalledStream();
        using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadStreamCopier.CopyAsync(
            source, destination, null, TimeSpan.FromSeconds(1), _ => { }, cancellation.Token));
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
