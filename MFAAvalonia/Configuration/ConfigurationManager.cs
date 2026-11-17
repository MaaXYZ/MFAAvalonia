using Avalonia.Collections;
using MFAAvalonia;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MFAAvalonia.Configuration;

public static class ConfigurationManager
{
    private const string DefaultConfigTemplateFileName = "config.template.json";
    private static Dictionary<string, Dictionary<string, object>> _presetSettings = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, object> _templateSettings = new(StringComparer.OrdinalIgnoreCase);
    public static BackgroundDefaults Background { get; private set; } = BackgroundDefaults.Empty;

    public static bool MigrateBackgroundDefaults()
    {
        if (!Current.IsLoadedSuccessfully) return false;
        var change = Background.CreateMigration(Current.Config);
        if (change == null) return false;
        return Current.TryCommitBackground(change);
    }

    private static (Dictionary<string, object> Values, bool Success) ReadUserConfig(string fileName)
    {
        var path = Path.Combine(ConfigDir, fileName + ".json");
        if (!File.Exists(path))
            return (new Dictionary<string, object> { [BackgroundDefaults.MigrationKey] = BackgroundDefaults.MigrationVersion }, true);
        var values = JsonHelper.LoadJson<Dictionary<string, object>?>(path, null);
        if (values == null) LoggerHelper.Warning($"用户配置未成功解析，禁用背景迁移和写入：{fileName}");
        return (values ?? new Dictionary<string, object>(), values != null);
    }

    private static string ConfigDir
    {
        get
        {
            AppPaths.Initialize();
            return AppPaths.ConfigDirectory;
        }
    }
    public static readonly MFAConfiguration Maa = new("Maa", "maa_option", new Dictionary<string, object>());
    public static MFAConfiguration Current = new("Default", "config", new Dictionary<string, object>());
    public static InstanceConfiguration CurrentInstance => MaaProcessorManager.Instance?.Current?.InstanceConfiguration ?? new InstanceConfiguration("default");

    public static AvaloniaList<MFAConfiguration> Configs { get; } = LoadConfigurations();

    public static event Action<string>? ConfigurationSwitched;

    public static bool IsSwitching { get; private set; }
    private static readonly object _switchLock = new();
    private static string? _pendingSwitchName;

    public static string ConfigName { get; set; }
    public static string GetCurrentConfiguration() => ConfigName;

    public static string GetActualConfiguration()
    {
        if (ConfigName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return "config";
        return $"mfa_{GetCurrentConfiguration()}";
    }

    public static void Initialize()
    {
        LoggerHelper.Info("当前配置：" + GetCurrentConfiguration());
    }

    public static void SwitchConfiguration(string? name)
    {
        _ = SwitchConfigurationAsync(name);
    }

    private static async Task SwitchConfigurationAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (ConfigName.Equals(name, StringComparison.OrdinalIgnoreCase))
            return;

        LoggerHelper.UserAction(
            "切换配置",
            $"from={ConfigName} -> to={name}",
            source: "UI",
            operation: "SwitchConfiguration",
            configName: ConfigName);

        if (!Configs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            LoggerHelper.Warning($"配置 {name} 不存在，切换已取消");
            return;
        }

        lock (_switchLock)
        {
            if (IsSwitching)
            {
                _pendingSwitchName = name;
                return;
            }
            IsSwitching = true;
        }

        if (Instances.RootViewModel.IsRunning)
        {
            ToastHelper.Warn(LangKeys.SwitchConfiguration.ToLocalization());
            LoggerHelper.Warning($"配置切换被拒绝，因为当前仍有任务正在运行：目标配置={name}");
            lock (_switchLock)
            {
                IsSwitching = false;
            }
            return;
        }

        await DispatcherHelper.RunOnMainThreadAsync(() =>
        {
            Instances.RootViewModel.SetConfigSwitchingState(true);
            Instances.RootViewModel.SetConfigSwitchProgress(5);
        });
        await Task.Run(() => MaaProcessorManager.Instance.Current.SetTasker());
        await Task.Delay(60);

        try
        {
            DispatcherHelper.PostOnMainThread(() => Instances.RootViewModel.SetConfigSwitchProgress(25));

            var config = Configs.First(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var configData = await Task.Run(() => ReadUserConfig(config.FileName));

            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                SetDefaultConfig(name);
                ConfigName = name;
                config.SetConfig(configData.Values, configData.Success);
                Current = config;
                MigrateBackgroundDefaults();
                Instances.RootViewModel.SetConfigSwitchProgress(55);
            });

            await DispatcherHelper.RunOnMainThreadAsync(() => ConfigurationSwitched?.Invoke(name));
            await Instances.ReloadConfigurationForSwitchAsync();
            LoggerHelper.Info($"配置切换完成：当前配置={ConfigName}");

            DispatcherHelper.PostOnMainThread(() => Instances.RootViewModel.SetConfigSwitchProgress(98));
        }
        finally
        {
            await DispatcherHelper.RunOnMainThreadAsync(() => Instances.RootViewModel.SetConfigSwitchProgress(100));
            await Task.Delay(120);
            DispatcherHelper.PostOnMainThread(() => Instances.RootViewModel.SetConfigSwitchingState(false));

            lock (_switchLock)
            {
                IsSwitching = false;
            }
        }

        string? pending;
        lock (_switchLock)
        {
            pending = _pendingSwitchName;
            _pendingSwitchName = null;
        }

        if (!string.IsNullOrWhiteSpace(pending) && !pending.Equals(ConfigName, StringComparison.OrdinalIgnoreCase))
        {
            await SwitchConfigurationAsync(pending);
        }
    }

    public static void SetDefaultConfig(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        GlobalConfiguration.SetValue(ConfigurationKeys.DefaultConfig, name);
    }

    public static string GetDefaultConfig()
    {
        return GlobalConfiguration.GetValue(ConfigurationKeys.DefaultConfig, "Default");
    }

    public static Dictionary<string, object> GetPresetSettings(params string?[] presetNames)
    {
        var names = presetNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (names.Length == 0)
            return new Dictionary<string, object>();

        try
        {
            object? value = _presetSettings.Count > 0
                ? _presetSettings
                : Current.Config.TryGetValue("PresetSettings", out var legacyValue) ? legacyValue : null;
            if (value is null)
                return new Dictionary<string, object>();

            var presets = value is Newtonsoft.Json.Linq.JObject obj
                ? obj.ToObject<Dictionary<string, Dictionary<string, object>>>()
                : JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(
                    JsonConvert.SerializeObject(value));
            if (presets == null)
                return new Dictionary<string, object>();

            foreach (var name in names)
            {
                var match = presets.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match.Value != null)
                    return match.Value;
            }

            return new Dictionary<string, object>();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取 preset 专属配置失败：{string.Join(", ", names)}，已忽略。{ex.Message}");
            return new Dictionary<string, object>();
        }
    }

    public static Dictionary<string, object> GetTemplateSettings()
        => new(_templateSettings, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, object> GetPresetSettings(int presetIndex, params string?[] presetNames)
    {
        var names = presetNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        names.Add(presetIndex.ToString());
        names.Add($"preset_{presetIndex}");
        return GetPresetSettings(names.ToArray());
    }

    private static AvaloniaList<MFAConfiguration> LoadConfigurations()
    {
        LoggerHelper.Info("正在加载配置列表...");
        ConfigName = GetDefaultConfig();

        var collection = new AvaloniaList<MFAConfiguration>();

        var configDir = ConfigDir;
        var defaultConfigPath = Path.Combine(configDir, "config.json");
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);
        TryPromoteDefaultConfigTemplate(configDir, defaultConfigPath);
        if (!File.Exists(defaultConfigPath))
        {
            var initial = new Dictionary<string, object>();
            if (!Directory.EnumerateFiles(configDir, "mfa_*.json", SearchOption.TopDirectoryOnly).Any())
                initial[BackgroundDefaults.MigrationKey] = BackgroundDefaults.MigrationVersion;
            if (!BackgroundConfigurationWriter.TryWrite(defaultConfigPath, initial, false, out var error))
                throw new IOException($"无法创建初始配置：{error}");
        }
        if (ConfigName != "Default" && !File.Exists(Path.Combine(configDir, $"mfa_{ConfigName}.json")))
            ConfigName = "Default";
        var defaultData = ReadUserConfig("config");
        collection.Add(Current.SetConfig(defaultData.Values, defaultData.Success));
        foreach (var file in Directory.EnumerateFiles(configDir, "mfa_*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName == "maa_option" || fileName == "config") continue;
            string nameWithoutPrefix = fileName.StartsWith("mfa_")
                ? fileName.Substring("mfa_".Length)
                : fileName;
            var configs = ReadUserConfig(fileName);

            var config = new MFAConfiguration(nameWithoutPrefix, fileName, configs.Values)
            {
                IsLoadedSuccessfully = configs.Success,
            };

            collection.Add(config);
        }

        Maa.SetConfig(JsonHelper.LoadConfig("maa_option", new Dictionary<string, object>()));

        Current = collection.FirstOrDefault(c
                => !string.IsNullOrWhiteSpace(c.Name)
                && c.Name.Equals(ConfigName, StringComparison.OrdinalIgnoreCase))
            ?? Current;

        return collection;
    }

    private static void TryPromoteDefaultConfigTemplate(string configDir, string defaultConfigPath)
    {
        Background = BackgroundDefaults.FromTemplate(new Newtonsoft.Json.Linq.JObject(), AppPaths.DataRoot,
            message => LoggerHelper.Warning(message));
        var templatePath = Path.Combine(configDir, DefaultConfigTemplateFileName);
        if (!File.Exists(templatePath))
            return;

        var template = JsonHelper.LoadJson<Newtonsoft.Json.Linq.JObject?>(templatePath, null);
        if (template == null)
        {
            LoggerHelper.Warning($"背景模板无效，保留文件并使用内置默认：{DefaultConfigTemplateFileName}");
            return;
        }
        Background = BackgroundDefaults.FromTemplate(template, AppPaths.DataRoot, message => LoggerHelper.Warning(message));
        LoggerHelper.Info($"已读取持续背景默认：[{string.Join(", ", Background.TemplateKeys)}]");

        // 只有应用实际使用的正式配置文件才会阻止预设接管；同目录下其它
        // 非 mfa 配置（例如 maa_option.json 或资源附带的 JSON）不应影响首次初始化。
        var hasExistingConfig = File.Exists(defaultConfigPath)
            || Directory.EnumerateFiles(configDir, "mfa_*.json", SearchOption.TopDirectoryOnly).Any();

        var instancesDir = Path.Combine(configDir, "instances");
        if (!hasExistingConfig && Directory.Exists(instancesDir))
        {
            hasExistingConfig = Directory.EnumerateFiles(instancesDir, "*.json", SearchOption.AllDirectories).Any();
        }

        if (hasExistingConfig)
        {
            LoggerHelper.Info("检测到已有用户配置，只读取持续背景默认，不重新应用首装种子。");
            return;
        }

        try
        {
            var templateConfig = template.ToObject<Dictionary<string, object>>() ?? new();
            templateConfig.TryGetValue("PresetSettings", out var presetSettings);
            templateConfig.Remove("PresetSettings");
            foreach (var key in templateConfig.Keys.Where(key => BackgroundDefaults.IsBackgroundKey(key)
                         || BackgroundDefaults.IsMetadataKey(key)).ToArray())
                templateConfig.Remove(key);
            _templateSettings = new Dictionary<string, object>(templateConfig, StringComparer.OrdinalIgnoreCase);
            templateConfig[BackgroundDefaults.MigrationKey] = BackgroundDefaults.MigrationVersion;
            if (!BackgroundConfigurationWriter.TryWrite(defaultConfigPath, templateConfig, false, out var error))
            {
                _templateSettings.Clear();
                LoggerHelper.Error($"首装配置保存失败，保留模板：{error}");
                return;
            }

            if (presetSettings is not null)
            {
                _presetSettings = presetSettings is Newtonsoft.Json.Linq.JObject presetObject
                    ? presetObject.ToObject<Dictionary<string, Dictionary<string, object>>>()
                        ?? new(StringComparer.OrdinalIgnoreCase)
                    : JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(
                        JsonConvert.SerializeObject(presetSettings))
                        ?? new(StringComparer.OrdinalIgnoreCase);
            }

            LoggerHelper.Info($"已写入首装种子并保留背景模板：[{string.Join(", ", _templateSettings.Keys)}]");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"临时预设配置转换失败：{DefaultConfigTemplateFileName}", ex);
        }
    }

    public static void SaveConfiguration(string configName)
    {
        var config = Configs.FirstOrDefault(c => c.Name == configName);
        if (config != null)
        {
            JsonHelper.SaveConfig(config.FileName, config.Config);
        }
    }

    public static MFAConfiguration Add(string name)
    {
        var configPath = ConfigDir;
        var newConfigPath = Path.Combine(configPath, $"{name}.json");
        var newConfig = new MFAConfiguration(name.Equals("config", StringComparison.OrdinalIgnoreCase) ? "Default" : name, name.Equals("config", StringComparison.OrdinalIgnoreCase) ? name : $"mfa_{name}",
            new Dictionary<string, object> { [BackgroundDefaults.MigrationKey] = BackgroundDefaults.MigrationVersion });
        Configs.Add(newConfig);
        return newConfig;
    }
}
