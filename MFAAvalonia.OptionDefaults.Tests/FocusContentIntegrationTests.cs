using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

// The integration tests temporarily capture the process-wide logger.
[CollectionDefinition("Focus content", DisableParallelization = true)]
public class FocusContentCollection;

[Collection("Focus content")]
public class FocusContentIntegrationTests(OptionControlsTestSession fixture) : IClassFixture<OptionControlsTestSession>
{
    [Theory]
    [InlineData("ResolveFocusContent")]
    [InlineData("ResolveLegacyFocusText")]
    public Task FocusHtmlDoesNotLogMissingFilesButRealReferencesDo(string entryPoint)
        => fixture.Session.Dispatch(() =>
        {
            using var context = new OptionControlsTestContext();
            var handler = new FocusHandler(new AutoInitDictionary(), context.TaskQueue);
            var method = typeof(FocusHandler).GetMethod(entryPoint, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var loggerField = typeof(LoggerHelper).GetField("_logger", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previousLogger = loggerField.GetValue(null);
            var sink = new RecordingSink();
            using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            loggerField.SetValue(null, logger);
            try
            {
                const string html = "<font color=\"DeepSkyBlue\">火影忍者，启动！</font>";
                object?[] arguments = entryPoint == "ResolveFocusContent" ? [html, null, null] : [html];
                Assert.Equal(html, method.Invoke(handler, arguments));
                Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning);

                var missing = $"./missing-focus-{Guid.NewGuid():N}.html";
                arguments[0] = missing;
                Assert.Equal(missing, method.Invoke(handler, arguments));
                Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Warning
                    && e.RenderMessage().Contains("Focus 内容文件不存在", StringComparison.Ordinal));
            }
            finally
            {
                loggerField.SetValue(null, previousLogger);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OriginalThreeParameterMethodRemainsAvailable()
    {
        var method = typeof(MFAExtensions).GetMethod("ResolveContentAsync",
            [typeof(string), typeof(string), typeof(bool)]);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method.ReturnType);
        Assert.Equal(string.Empty, await (Task<string>)method.Invoke(null, [null, null, true])!);
    }

    [Fact]
    public Task RealHttpFetchDoesNotPostBackToBlockedCallingContext()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        return fixture.Session.Dispatch(() =>
        {
            using var context = new OptionControlsTestContext();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
            cancellation.CancelAfter(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                // Force the production HTTP await to complete asynchronously.
                await Task.Delay(50, cancellation.Token).ConfigureAwait(false);
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nHTML");
                await stream.WriteAsync(response, cancellation.Token);
            }, cancellation.Token);

            var previousContext = SynchronizationContext.Current;
            var recordingContext = new RecordingContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(recordingContext);
                var download = $"http://127.0.0.1:{port}/notice.html".ResolveContentAsync();
                Assert.True(download.Wait(TimeSpan.FromSeconds(5)), "HTTP completion required the blocked calling context.");
                Assert.Equal("HTML", download.GetAwaiter().GetResult());
                Assert.Equal(0, recordingContext.Posts);
                server.GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                cancellation.Cancel();
            }
        }, testCancellation);
    }

    private sealed class RecordingSink : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            // Let an incorrect implementation finish too, so failure is bounded
            // and does not leave a deadlocked task behind in the test runner.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
