using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MFAAvalonia.Configuration;

/// <summary>Write a complete task-state candidate before publishing it in memory.</summary>
public static class TaskConfigurationWriter
{
    public const string BackupSuffix = ".before-task-state-v1.bak";

    public static bool TryWrite(string path, Dictionary<string, object> values, bool migration,
        out string? error, params JsonConverter[] converters)
    {
        var temporary = path + ".tasks-" + Guid.NewGuid().ToString("N") + ".tmp";
        error = null;
        try
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Include,
                DefaultValueHandling = DefaultValueHandling.Include,
            };
            foreach (var converter in converters) settings.Converters.Add(converter);
            var json = JsonConvert.SerializeObject(values, settings);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (migration && File.Exists(path))
            {
                var backup = path + BackupSuffix;
                if (!File.Exists(backup)) File.Copy(path, backup, false);
            }
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return true;
        }
        catch (Exception ex)
        {
            // Values can contain credentials. Report the failing operation/type, not serialized data.
            error = $"任务配置写入失败 ({ex.GetType().Name})：{path}";
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* The original configuration remains authoritative. */ }
        }
    }
}
