using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Configuration;

public partial class MFAConfiguration(string name, string fileName, Dictionary<string, object> config) : ObservableObject
{
    [ObservableProperty] private string _name = name;

    [ObservableProperty] private string _fileName = fileName;

    [ObservableProperty] private Dictionary<string, object> _config = config;

    public bool IsLoadedSuccessfully { get; internal set; } = true;

    public bool TryCommitBackground(BackgroundDefaults.Change change)
    {
        if (!IsLoadedSuccessfully)
        {
            LoggerHelper.Warning("用户配置解析失败，未保存背景修改。");
            return false;
        }
        if (!BackgroundConfigurationWriter.TryWrite(GetConfigFilePath(), change.Values, change.IsMigration,
                out var error, new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false)))
        {
            LoggerHelper.Error($"背景配置保存失败，保留原设置：{error}");
            return false;
        }
        Config = change.Values;
        if (change.IsMigration)
            LoggerHelper.Info($"背景默认值迁移已保存：删除字段=[{string.Join(", ", change.RemovedKeys)}]");
        return true;
    }


    [RelayCommand]
    private void DeleteConfiguration()
    {
        if (ConfigurationManager.Configs.All(c => c.Name != Name))
        {
            LoggerHelper.Error($"配置 {Name} 不存在");
            return;
        }

        if (ConfigurationManager.ConfigName == Name)
        {
            LoggerHelper.Error($"不能删除当前使用的配置 {Name}");
            return;
        }

        ConfigurationManager.Configs.Remove(this);
        File.Delete(GetConfigFilePath());
    }

    private string GetConfigFilePath() =>
        Path.Combine(
            AppPaths.ConfigDirectory,
            $"{FileName}.json");

        public void SetValue(string key, object? value)
        {
            if (Config == null || value == null) return;
            // 移除配置切换时阻止 TaskItems 保存的逻辑
            // 这会导致任务配置无法正确更新到新配置中
            Config[key] = value;
            JsonHelper.SaveConfig(FileName, Config, new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false));
        }

    public bool ContainsKey(string key) => Config.ContainsKey(key);
    public T GetValue<T>(string key, T defaultValue, List<T> whitelist)
    {
        var value = GetValue(key, defaultValue);
        return whitelist.Contains(value) ? value : defaultValue;
    }
    
    public T GetValue<T>(string key, T defaultValue)
    {
        if (Config.TryGetValue(key, out var data))
        {
            try
            {
                if (data is long longValue && typeof(T) == typeof(int))
                {
                    return (T)(object)Convert.ToInt32(longValue);
                }

                if (data is T t)
                {
                    return t;
                }

                if (data is JArray jArray)
                {
                    // 使用 JsonConvert 进行反序列化，确保嵌套对象也能正确处理
                    return JsonConvert.DeserializeObject<T>(jArray.ToString()) ?? defaultValue;
                }

                if (data is JObject jObject)
                {
                    // 使用 JsonConvert 进行反序列化，确保嵌套对象也能正确处理
                    return JsonConvert.DeserializeObject<T>(jObject.ToString()) ?? defaultValue;
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Error("在进行类型转换时发生错误!", e);
            }
        }

        return defaultValue;
    }
    public T GetValue<T>(string key, T defaultValue, Dictionary<object, T> options)
    {

        if (Config.TryGetValue(key, out var data))
        {
            if (options != null && options.TryGetValue(data, out var result))
            {
                return result;
            }
            try
            {
                if (data is long longValue && typeof(T) == typeof(int))
                {
                    return (T)(object)Convert.ToInt32(longValue);
                }

                if (data is T t)
                {
                    return t;
                }

                if (data is JArray jArray)
                {
                    // 使用 JsonConvert 进行反序列化，确保嵌套对象也能正确处理
                    return JsonConvert.DeserializeObject<T>(jArray.ToString()) ?? defaultValue;
                }

                if (data is JObject jObject)
                {
                    // 使用 JsonConvert 进行反序列化，确保嵌套对象也能正确处理
                    return JsonConvert.DeserializeObject<T>(jObject.ToString()) ?? defaultValue;
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Error("在进行类型转换时发生错误!", e);
            }
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, T? noValue = default, params JsonConverter[] valueConverters)
    {

        if (Config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                {
                    settings.Converters.Add(converter);
                }
                var result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? defaultValue;
                if (result.Equals(noValue))
                    return defaultValue;
                return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
                return defaultValue;
            }
        }
        return defaultValue;
    }
    public T GetValue<T>(string key, T defaultValue, List<T>? noValue = null, params JsonConverter[] valueConverters)
    {

        if (Config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                {
                    settings.Converters.Add(converter);
                }
                var result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? defaultValue;
                if (noValue != null && noValue.Contains(result))
                    return defaultValue;
                return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
                return defaultValue;
            }
        }
        return defaultValue;
    }
    public bool TryGetValue<T>(string key, out T output, params JsonConverter[] valueConverters)
    {
        if (Config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                {
                    settings.Converters.Add(converter);
                }
                output = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? default;
                return true;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
            }
        }
        output = default;
        return false;
    }
    public string GetDecrypt(string key, string defaultValue = "") => SimpleEncryptionHelper.Decrypt(GetValue(key, defaultValue));
    public void SetEncrypted(string key, string value) =>
        SetValue(key, SimpleEncryptionHelper.Encrypt(value));


    public override string ToString() => Name;

    public MFAConfiguration SetName(string name)
    {
        Name = name;
        return this;
    }

    public MFAConfiguration SetFileName(string name)
    {
        FileName = name;
        return this;
    }

    public MFAConfiguration SetConfig(Dictionary<string, object> config, bool loadedSuccessfully = true)
    {
        Config = config;
        IsLoadedSuccessfully = loadedSuccessfully;
        return this;
    }
}
