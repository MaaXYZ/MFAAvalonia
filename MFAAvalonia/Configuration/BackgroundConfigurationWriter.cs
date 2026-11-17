using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MFAAvalonia.Configuration;

/// <summary>Commit background edits only after the complete file was safely written.</summary>
public static class BackgroundConfigurationWriter
{
    public static bool TryWrite(string path, Dictionary<string, object> values, bool migration,
        out string? error, params JsonConverter[] converters)
    {
        var temporary = path + ".background-" + Guid.NewGuid().ToString("N") + ".tmp";
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
                var backup = path + ".before-background-defaults-v1.bak";
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
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* The original configuration remains authoritative. */ }
        }
    }
}
