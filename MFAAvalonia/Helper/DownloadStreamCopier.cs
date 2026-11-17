using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

internal static class DownloadStreamCopier
{
    // Limit inactivity rather than total duration: slow downloads may take hours.
    internal static async Task CopyAsync(Stream source, Stream destination, long? expectedLength,
        TimeSpan idleTimeout, Action<long> reportProgress, CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            idle.CancelAfter(idleTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(), idle.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"下载连续 {idleTimeout.TotalSeconds} 秒未收到数据。", ex);
            }
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            reportProgress(total);
        }

        if (total == 0 || (expectedLength.HasValue && total != expectedLength.Value))
            throw new IOException($"下载文件不完整：预期={expectedLength}，实际={total} 字节。");
        await destination.FlushAsync(cancellationToken);
    }
}
