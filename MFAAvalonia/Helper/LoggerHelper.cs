using Avalonia.Controls;
using MFAAvalonia.Configuration;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MFAAvalonia.Helper;

public static class LoggerHelper
{
    private static volatile Serilog.Core.Logger? _logger;
    private static readonly object LoggerLock = new();
    private static readonly List<(LogEventLevel level, string message, Exception? exception)> _logCache = [];
    private static readonly AsyncLocal<LogContextFrame?> CurrentContext = new();

    private readonly record struct LogContextFrame(
        string? Source,
        string? Operation,
        string? InstanceId,
        string? InstanceName,
        string? ConfigName);

    private sealed class LogContextScope(LogContextFrame? previous) : IDisposable
    {
        private readonly LogContextFrame? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentContext.Value = _previous;
            _disposed = true;
        }
    }

    public static void InitializeLogger()
    {
        if (Design.IsDesignMode)
            return;

        lock (LoggerLock)
        {
            if (_logger != null)
                return;

            AppPaths.Initialize();
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(AppPaths.LogsDirectory, "log-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            FlushCache(logger);
            _logger = logger;
        }
    }

    public static void DisposeLogger()
    {
        lock (LoggerLock)
        {
            _logger?.Dispose();
            _logger = null;
        }
    }

    public static IDisposable PushContext(
        string? source = null,
        string? operation = null,
        string? instanceId = null,
        string? instanceName = null,
        string? configName = null)
    {
        var previous = CurrentContext.Value;
        var merged = new LogContextFrame(
            string.IsNullOrWhiteSpace(source) ? previous?.Source : source,
            string.IsNullOrWhiteSpace(operation) ? previous?.Operation : operation,
            string.IsNullOrWhiteSpace(instanceId) ? previous?.InstanceId : instanceId,
            string.IsNullOrWhiteSpace(instanceName) ? previous?.InstanceName : instanceName,
            string.IsNullOrWhiteSpace(configName) ? previous?.ConfigName : configName);

        CurrentContext.Value = merged;
        return new LogContextScope(previous);
    }

    public static void UserAction(
        string action,
        string? details = null,
        string? source = "UI",
        string? operation = null,
        string? instanceId = null,
        string? instanceName = null,
        string? configName = null)
    {
        using var _ = PushContext(
            source: source,
            operation: string.IsNullOrWhiteSpace(operation) ? action : operation,
            instanceId: instanceId,
            instanceName: instanceName,
            configName: configName);

        var message = string.IsNullOrWhiteSpace(details)
            ? $"用户操作: {action}"
            : $"用户操作: {action} | {details}";

        Write(LogEventLevel.Information, message);
    }

    public static void Info(object? message) => Write(LogEventLevel.Information, message);

    public static void Debug(object? message) => Write(LogEventLevel.Debug, message);

    public static void Error(object? message)
    {
        if (message is Exception exception)
        {
            Write(LogEventLevel.Error, exception.Message, exception);
            return;
        }

        Write(LogEventLevel.Error, message);
    }

    public static void Error(object? message, Exception exception) => Write(LogEventLevel.Error, message, exception);

    public static void Warn(object? message) => Write(LogEventLevel.Warning, message);

    public static void Warning(object? message)
    {
        if (message is Exception exception)
        {
            Write(LogEventLevel.Warning, exception.Message, exception);
            return;
        }

        Write(LogEventLevel.Warning, message);
    }

    private static void FlushCache(Serilog.Core.Logger logger)
    {
        foreach (var (level, message, exception) in _logCache)
        {
            logger.Write(level, exception, message);
        }

        _logCache.Clear();
    }

    private static void Write(LogEventLevel level, object? message, Exception? exception = null)
    {
        var renderedMessage = NormalizeMessage(RenderMessage(message));
        var formattedMessage = AddContextPrefix(renderedMessage);

        var logger = _logger;
        if (logger == null)
        {
            lock (LoggerLock)
            {
                logger = _logger;
                if (logger == null)
                {
                    _logCache.Add((level, formattedMessage, exception));
                    return;
                }
            }
        }

        logger.Write(level, exception, formattedMessage);
    }

    private static string RenderMessage(object? message)
    {
        if (message is null)
            return string.Empty;

        if (message is Exception exception)
            return exception.Message;

        return message.ToString() ?? string.Empty;
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        if (ContainsCjk(message))
        {
            message = message.Replace(": ", "：")
                .Replace(" (", "（")
                .Replace(") ", "）")
                .Replace(")\n", "）\n");
        }

        return message;
    }

    private static bool ContainsCjk(string message)
    {
        foreach (var ch in message)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
                return true;
        }

        return false;
    }

    private static string AddContextPrefix(string message)
    {
        var context = CurrentContext.Value;
        var parts = new List<string>();

        var configName = context?.ConfigName;
        if (string.IsNullOrWhiteSpace(configName))
        {
            try
            {
                configName = ConfigurationManager.GetCurrentConfiguration();
            }
            catch
            {
                configName = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(configName))
            parts.Add($"cfg={configName}");

        var instanceToken = BuildInstanceToken(context);
        if (!string.IsNullOrWhiteSpace(instanceToken))
            parts.Add($"inst={instanceToken}");

        if (!string.IsNullOrWhiteSpace(context?.Source))
            parts.Add($"src={context.Value.Source}");

        if (!string.IsNullOrWhiteSpace(context?.Operation))
            parts.Add($"op={context.Value.Operation}");

        if (parts.Count == 0)
            return message;

        return $"[{string.Join("][", parts)}] {message}";
    }

    private static string? BuildInstanceToken(LogContextFrame? context)
    {
        if (context == null)
            return null;

        var instanceName = context.Value.InstanceName;
        var instanceId = context.Value.InstanceId;

        if (!string.IsNullOrWhiteSpace(instanceName) && !string.IsNullOrWhiteSpace(instanceId))
            return $"{instanceName}/{instanceId}";

        return !string.IsNullOrWhiteSpace(instanceName) ? instanceName : instanceId;
    }
}
