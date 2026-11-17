using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Core.Tests
{
    public class TaskManagerTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailureDoesNotLogCompletion(bool asynchronous)
        {
            LoggerHelper.Messages.Value = new List<string>();
            var handled = false;
            if (asynchronous)
                await TaskManager.RunTaskAsync((Func<Task>)(() => Task.FromException(new IOExceptionForTest())),
                    handleError: () => handled = true);
            else
                await TaskManager.RunTaskAsync((Action)(() => throw new IOExceptionForTest()),
                    handleError: () => handled = true);
            Assert.True(handled);
            Assert.Contains(LoggerHelper.Messages.Value, message => message.Contains("执行失败"));
            Assert.DoesNotContain(LoggerHelper.Messages.Value, message => message.Contains("完成"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SuccessLogsCompletion(bool asynchronous)
        {
            LoggerHelper.Messages.Value = new List<string>();
            if (asynchronous)
                await TaskManager.RunTaskAsync((Func<Task>)(() => Task.CompletedTask));
            else
                await TaskManager.RunTaskAsync((Action)(() => { }));
            Assert.Contains(LoggerHelper.Messages.Value, message => message.Contains("完成"));
        }

        [Fact]
        public async Task UnhandledFailurePropagatesWithoutCompletion()
        {
            LoggerHelper.Messages.Value = new List<string>();
            await Assert.ThrowsAsync<IOExceptionForTest>(() => TaskManager.RunTaskAsync(
                (Func<Task>)(() => Task.FromException(new IOExceptionForTest())), catchException: false));
            Assert.DoesNotContain(LoggerHelper.Messages.Value, message => message.Contains("完成"));
        }

        private sealed class IOExceptionForTest : Exception;
    }
}

namespace MFAAvalonia.Helper
{
    // Capture production TaskManager logging without starting the Avalonia host.
    internal static class LoggerHelper
    {
        internal static readonly AsyncLocal<List<string>> Messages = new();
        public static void Info(string message) => Messages.Value?.Add(message);
        public static void Error(string message, Exception exception) => Messages.Value?.Add(message);
    }
}
