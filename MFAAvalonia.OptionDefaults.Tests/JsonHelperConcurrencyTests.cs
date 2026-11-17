using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

[Collection("Focus content")]
public class JsonHelperConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSavesOwnTheirTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mfa-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "config.json");
        var loggerField = typeof(LoggerHelper).GetField("_logger", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousLogger = loggerField.GetValue(null);
        var sink = new RecordingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        loggerField.SetValue(null, logger);
        try
        {
            await Task.WhenAll(Enumerable.Range(1, 32).Select(writer => Task.Run(() =>
            {
                for (var iteration = 0; iteration < 10; iteration++)
                    JsonHelper.SaveJson(file, new { Writer = writer, Payload = new string((char)('A' + writer % 26), 8192) });
            })));

            Assert.DoesNotContain(sink.Events, entry => entry.Level >= LogEventLevel.Error);
            var saved = JObject.Parse(File.ReadAllText(file));
            var writer = saved.Value<int>("Writer");
            Assert.InRange(writer, 1, 32);
            Assert.Equal(new string((char)('A' + writer % 26), 8192), saved.Value<string>("Payload"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            loggerField.SetValue(null, previousLogger);
            Directory.Delete(directory, true);
        }
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
