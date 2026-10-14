using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Configuration;

public static class GlobalConfiguration
{
    private static readonly object _fileLock = new();
    private static string ConfigFilePath
    {
        get
        {
            AppPaths.Initialize();
            return AppPaths.GlobalConfigPath;
        }
    }

    public static string ConfigPath => ConfigFilePath;
    public static bool HasFileAccessError { get; private set; }
    public static string? LastFileAccessErrorMessage { get; private set; }

    private static IConfigurationRoot LoadConfiguration()
    {
        try
        {
            var configPath = ConfigFilePath;
            if (!File.Exists(configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, "{}");
            }

            var builder = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(configPath))
                .AddJsonFile(configPath, optional: false, reloadOnChange: false);

            return builder.Build();
        }
        catch (InvalidDataException ex)
        {
            ReportFileAccessError(ex);
        }
        catch (IOException ex)
        {
            ReportFileAccessError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportFileAccessError(ex);
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();
    }

    public static void SetValue(string key, string value)
    {
        lock (_fileLock)
        {
            try
            {
                var configDict = new Dictionary<string, string>();
                var configPath = ConfigFilePath;
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    configDict = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                        ?? new Dictionary<string, string>();
                }

                configDict[key] = value;

                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath,
                    JsonSerializer.Serialize(configDict, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
            }
            catch (IOException ex)
            {
                ReportFileAccessError(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                ReportFileAccessError(ex);
            }
        }
    }

    public static void RemoveValues(IEnumerable<string> keys)
    {
        lock (_fileLock)
        {
            try
            {
                var configPath = ConfigFilePath;
                if (!File.Exists(configPath)) return;

                var json = File.ReadAllText(configPath);
                var configDict = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
                var changed = false;
                foreach (var key in keys)
                    changed |= configDict.Remove(key);

                if (!changed) return;

                File.WriteAllText(configPath,
                    JsonSerializer.Serialize(configDict, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
            }
            catch (IOException ex)
            {
                ReportFileAccessError(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                ReportFileAccessError(ex);
            }
        }
    }

    public static string GetValue(string key, string defaultValue = "")
    {
        var config = LoadConfiguration();
        return config[key] ?? defaultValue;
    }

    private static void ReportFileAccessError(Exception ex)
    {
        HasFileAccessError = true;
        LastFileAccessErrorMessage = ex.Message;
        LoggerHelper.Error($"全局配置文件访问失败: {ConfigFilePath}", ex);
    }

    public static string GetTimer(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}", defaultValue);
    }

    public static void SetTimer(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}", value);
    }

    public static string GetTimerTime(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}Time", defaultValue);
    }

    public static void SetTimerTime(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}Time", value);
    }

    public static string GetTimerConfig(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}.Config", defaultValue);
    }

    public static void SetTimerConfig(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}.Config", value);
    }

    public static string GetTimerSchedule(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}.Schedule", defaultValue);
    }

    public static void SetTimerSchedule(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}.Schedule", value);
    }

    public static string GetTimerAction(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}.Action", defaultValue);
    }

    public static void SetTimerAction(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}.Action", value);
    }

    public static int GetTimerCount(int defaultValue = 8)
    {
        var val = GetValue("Timer.Count", defaultValue.ToString());
        return int.TryParse(val, out var count) ? count : defaultValue;
    }

    public static void SetTimerCount(int value)
    {
        SetValue("Timer.Count", value.ToString());
    }

    public static string GetTimerStopConnectedProcess(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}.StopConnectedProcess", defaultValue);
    }

    public static void SetTimerStopConnectedProcess(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}.StopConnectedProcess", value);
    }

    public static string GetTimerStopMFA(int i, string defaultValue)
    {
        return GetValue($"Timer.Timer{i + 1}.StopMFA", defaultValue);
    }

    public static void SetTimerStopMFA(int i, string value)
    {
        SetValue($"Timer.Timer{i + 1}.StopMFA", value);
    }

    public static void RemoveTimerConfig(int i)
    {
        // 清除指定定时器的所有配置项
        SetValue($"Timer.Timer{i + 1}", string.Empty);
        SetValue($"Timer.Timer{i + 1}Time", string.Empty);
        SetValue($"Timer.Timer{i + 1}.Config", string.Empty);
        SetValue($"Timer.Timer{i + 1}.Schedule", string.Empty);
        SetValue($"Timer.Timer{i + 1}.Action", string.Empty);
        SetValue($"Timer.Timer{i + 1}.StopConnectedProcess", string.Empty);
        SetValue($"Timer.Timer{i + 1}.StopMFA", string.Empty);
    }
}
