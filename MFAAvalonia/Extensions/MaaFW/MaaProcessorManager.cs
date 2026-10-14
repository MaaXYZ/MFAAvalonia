using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

public sealed class MaaProcessorManager
{
    private static readonly Lazy<MaaProcessorManager> LazyInstance = new(() => new MaaProcessorManager());
    public static MaaProcessorManager Instance => LazyInstance.Value;
    public static bool IsInstanceCreated => LazyInstance.IsValueCreated;

    private readonly Dictionary<string, MaaProcessor> _instances = new();
    private readonly Dictionary<string, string> _instanceNames = new();
    private readonly List<string> _instanceOrder = new();
    private readonly Dictionary<string, TaskQueueViewModel> _viewModels = new();
    private readonly HashSet<string> _initializedInstances = new();
    private readonly Dictionary<string, int> _instancePresetIndexes = new(); // 存储实例对应的 preset 索引
    private readonly object _lock = new();

    public MaaProcessor Current { get; private set; }

    private MaaProcessorManager()
    {
        // 构造函数初始化默认状态，后续 LoadInstanceConfig 可覆盖
        // 注意：不调用 SetValue 写磁盘，避免每次启动都创建 default.json
        // 导致 ScanUnregisteredInstanceFiles 误将其识别为新实例
        Current = CreateInstanceInternal("default", setCurrent: true);
        var defaultName = $"{LangKeys.Config.ToLocalization()} 1";
        _instanceNames["default"] = defaultName;
        _instanceOrder.Add("default");
    }

    public IReadOnlyCollection<MaaProcessor> Instances
    {
        get
        {
            lock (_lock)
            {
                var list = new List<MaaProcessor>();
                // 按顺序添加
                foreach (var id in _instanceOrder)
                {
                    if (_instances.TryGetValue(id, out var processor))
                    {
                        list.Add(processor);
                    }
                }
                // 添加可能遗漏的（不在_instanceOrder中的）
                foreach (var kvp in _instances)
                {
                    if (!_instanceOrder.Contains(kvp.Key))
                    {
                        list.Add(kvp.Value);
                    }
                }
                return list.AsReadOnly();
            }
        }
    }

    public MaaProcessor CreateInstance(bool setCurrent = true)
    {
        return CreateInstance(CreateUniqueId(), setCurrent);
    }

    public MaaProcessor CreateInstance(string instanceId, bool setCurrent = true)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instanceId = CreateUniqueId();
        }

        var processor = CreateInstanceInternal(instanceId, setCurrent);

        lock (_lock)
        {
            if (!_instanceOrder.Contains(instanceId))
            {
                _instanceOrder.Add(instanceId);
                if (!_instanceNames.ContainsKey(instanceId))
                {
                    var name = InstanceConfiguration.ReadValueFromFile(instanceId, ConfigurationKeys.InstanceName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var configName = LangKeys.Config.ToLocalization();
                        var nextNumber = GetNextInstanceNumber();
                        name = $"{configName} {nextNumber}";
                    }

                    _instanceNames[instanceId] = name;
                    processor.InstanceConfiguration.SetValue(ConfigurationKeys.InstanceName, name);
                }
                SaveInstanceConfig();
            }
        }

        return processor;
    }

    public bool TryGetInstance(string instanceId, out MaaProcessor processor)
    {
        lock (_lock)
        {
            return _instances.TryGetValue(instanceId, out processor!);
        }
    }

    public bool SwitchCurrent(string instanceId)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(instanceId, out var processor))
                return false;

            Current = processor;

            SaveInstanceConfig();
        }

        return true;
    }

    public void PersistCurrentSelection()
    {
        lock (_lock)
        {
            SaveInstanceConfig();
        }
    }

    private MaaProcessor CreateInstanceInternal(string instanceId, bool setCurrent)
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(instanceId, out var existing))
            {
                if (setCurrent)
                {
                    Current = existing;
                }

                return existing;
            }

            var vm = new TaskQueueViewModel(instanceId);
            var processor = vm.Processor;
            _viewModels[instanceId] = vm;
            _instances[instanceId] = processor;

            if (setCurrent)
            {
                Current = processor;
            }

            return processor;
        }
    }

    private string CreateUniqueId()
    {
        string id;
        lock (_lock)
        {
            do
            {
                id = CreateInstanceId();
            } while (_instances.ContainsKey(id));
        }

        return id;
    }

    public static string CreateInstanceId()
    {
        var id = Guid.NewGuid().ToString("N");
        return id.Length > 8 ? id[..8] : id;
    }

    /// <summary>
    /// 获取下一个可用的实例编号
    /// </summary>
    private int GetNextInstanceNumber()
    {
        var configName = LangKeys.Config.ToLocalization();
        var usedNumbers = new HashSet<int>();

        foreach (var name in _instanceNames.Values)
        {
            if (name.StartsWith(configName))
            {
                var suffix = name[configName.Length..].Trim();
                if (int.TryParse(suffix, out var num))
                {
                    usedNumbers.Add(num);
                }
            }
        }

        // 找到最小的未使用编号
        var next = 1;
        while (usedNumbers.Contains(next))
            next++;
        return next;
    }

    private string CreateDefaultInstanceName()
    {
        return $"{LangKeys.Config.ToLocalization()} {GetNextInstanceNumber()}";
    }

    private static bool TryValidateInstanceFile(string instanceId, out string reason)
    {
        var filePath = Path.Combine(InstanceConfiguration.InstancesDir, $"{instanceId}.json");
        reason = string.Empty;

        try
        {
            if (!File.Exists(filePath))
            {
                reason = "实例文件不存在";
                return false;
            }

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                reason = "实例文件为空";
                return false;
            }

            var token = JToken.Parse(json);
            if (token is not JObject root)
            {
                reason = "实例文件根节点不是 JSON 对象";
                return false;
            }

            if (root.TryGetValue(ConfigurationKeys.InstanceName, out var instanceNameToken)
                && instanceNameToken.Type is not JTokenType.String and not JTokenType.Null)
            {
                reason = $"{ConfigurationKeys.InstanceName} 字段类型无效: {instanceNameToken.Type}";
                return false;
            }

            if (root.TryGetValue(ConfigurationKeys.TaskItems, out var taskItemsToken)
                && taskItemsToken.Type is not JTokenType.Array and not JTokenType.Null)
            {
                reason = $"{ConfigurationKeys.TaskItems} 字段类型无效: {taskItemsToken.Type}";
                return false;
            }

            if (root.TryGetValue(ConfigurationKeys.CurrentTasks, out var currentTasksToken)
                && currentTasksToken.Type is not JTokenType.Array and not JTokenType.Null)
            {
                reason = $"{ConfigurationKeys.CurrentTasks} 字段类型无效: {currentTasksToken.Type}";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            reason = $"JSON 解析失败: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"读取实例文件失败: {ex.Message}";
            return false;
        }
    }

    private static bool IsBootstrapDefaultInstance(string instanceId)
    {
        var filePath = Path.Combine(InstanceConfiguration.InstancesDir, $"{instanceId}.json");
        if (!File.Exists(filePath))
            return false;

        try
        {
            var token = JToken.Parse(File.ReadAllText(filePath));
            if (token is not JObject root)
                return false;

            if (root.TryGetValue(ConfigurationKeys.InstanceName, out var instanceNameToken)
                && !string.IsNullOrWhiteSpace(instanceNameToken?.ToString()))
            {
                return false;
            }

            if (root.TryGetValue(ConfigurationKeys.TaskItems, out var taskItemsToken)
                && taskItemsToken.Type == JTokenType.Array
                && taskItemsToken.HasValues)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public MFAAvalonia.ViewModels.Pages.TaskQueueViewModel? GetViewModel(string instanceId)
    {
        lock (_lock)
        {
            if (_viewModels.TryGetValue(instanceId, out var vm))
                return vm;

            // 已被移除的实例不再自动创建
            if (!_instanceOrder.Contains(instanceId))
                return null;

            vm = new MFAAvalonia.ViewModels.Pages.TaskQueueViewModel(instanceId);
            _viewModels[instanceId] = vm;
            _instances[instanceId] = vm.Processor;
            return vm;
        }
    }

    public string GetInstanceName(string instanceId)
    {
        lock (_lock)
        {
            if (_instanceNames.TryGetValue(instanceId, out var name))
                return name;
            return instanceId;
        }
    }

    public void SetInstanceName(string instanceId, string name)
    {
        lock (_lock)
        {
            _instanceNames[instanceId] = name;
            // 写入实例独立配置文件
            if (_instances.TryGetValue(instanceId, out var processor))
            {
                processor.InstanceConfiguration.SetValue(ConfigurationKeys.InstanceName, name);
            }
        }
    }

    public bool RemoveInstance(string instanceId)
    {
        lock (_lock)
        {
            if (_instances.Count <= 1)
                return false; // 不能删除最后一个实例

            if (_instances.TryGetValue(instanceId, out var processor))
            {
                // 如果删除的是当前实例，切换到其他实例
                if (Current.InstanceId == instanceId)
                {
                    var otherId = _instanceOrder.FirstOrDefault(id => id != instanceId)
                        ?? _instances.Keys.FirstOrDefault(k => k != instanceId);

                    if (otherId != null && _instances.TryGetValue(otherId, out var other))
                    {
                        Current = other;
                    }
                }

                // 删除实例独立配置文件 config/instances/{id}.json
                processor.InstanceConfiguration.DeleteConfigFile();

                if (_viewModels.TryGetValue(instanceId, out var viewModel))
                {
                    viewModel.Dispose();
                }

                processor.Dispose();
                _instances.Remove(instanceId);
                _instanceNames.Remove(instanceId);
                _instanceOrder.Remove(instanceId);
                _viewModels.Remove(instanceId);

                SaveInstanceConfig();
                return true;
            }
        }
        return false;
    }

    private void SaveInstanceConfig(bool saveLastActive = true)
    {
        // 使用 _instanceOrder 作为实例列表源，因为迁移阶段实例已注册到 _instanceOrder 但尚未创建到 _instances
        var list = string.Join(",", _instanceOrder);
        var order = string.Join(",", _instanceOrder);

        GlobalConfiguration.SetValue(ConfigurationKeys.InstanceList, list);
        GlobalConfiguration.SetValue(ConfigurationKeys.InstanceOrder, order);

        if (!saveLastActive || Current == null)
            return;

        var lastActiveName = _instanceNames.TryGetValue(Current.InstanceId, out var name)
            ? name
            : Current.InstanceId;

        GlobalConfiguration.SetValue(ConfigurationKeys.LastActiveInstance, Current.InstanceId);
        GlobalConfiguration.SetValue(ConfigurationKeys.LastActiveInstanceName, lastActiveName);
    }

    /// <summary>
    /// 更新实例顺序（拖拽排序后调用）
    /// </summary>
    public void UpdateInstanceOrder(IEnumerable<string> orderedIds)
    {
        lock (_lock)
        {
            _instanceOrder.Clear();
            foreach (var id in orderedIds)
            {
                if (_instances.ContainsKey(id) || _pendingInstanceIds.Contains(id))
                    _instanceOrder.Add(id);
            }
            SaveInstanceConfig();
        }
    }
    /// <summary>
    /// 需要延迟加载的实例ID列表
    /// </summary>
    private readonly List<string> _pendingInstanceIds = new();
    private bool _isLazyLoadingComplete;

    /// <summary>
    /// 迁移旧的 mfa_*.json 配置文件到多实例系统
    /// </summary>
    private void MigrateLegacyConfigs()
    {
        var configDir = AppPaths.ConfigDirectory;
        if (!Directory.Exists(configDir)) return;

        var instancesDir = Path.Combine(configDir, "instances");
        if (!Directory.Exists(instancesDir))
            Directory.CreateDirectory(instancesDir);

        // 第一步：如果当前活跃配置不是 Default，先把 mfa_xx.json 挪到 config.json
        MigrateActiveConfig(configDir);

        // 第二步：从 config.json 中提取已有的 Instance.{id}.* scoped keys 到独立文件
        MigrateScopedKeysToFiles(instancesDir);

        // 第三步：将 config.json 中无前缀的 plain instance-scoped keys 迁移到 default 实例文件
        MigratePlainKeysToDefaultInstance(instancesDir);

        // 第四步：将剩余的 mfa_*.json 迁移为多实例（直接写入独立文件）
        MigrateRemainingConfigs(configDir, instancesDir);

        // 迁移完成后，重新加载已创建实例的内存配置（构造函数创建的 default 实例可能已过期）
        foreach (var processor in _instances.Values)
        {
            processor.InstanceConfiguration.ReloadFromDisk();
        }
    }

    /// <summary>
    /// 如果当前活跃配置是 mfa_xx.json，将其内容合并到 config.json 并删除
    /// </summary>
    private void MigrateActiveConfig(string configDir)
    {
        var configName = ConfigurationManager.ConfigName;
        if (string.IsNullOrEmpty(configName)
            || configName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return;

        var activeFileName = $"mfa_{configName}";
        var activeFilePath = Path.Combine(configDir, $"{activeFileName}.json");
        if (!File.Exists(activeFilePath)) return;

        try
        {
            LoggerHelper.Info($"[迁移] 当前活跃配置为 '{configName}'，将 {activeFileName}.json 合并到 config.json...");

            var activeData = ConfigurationManager.Current.Config;

            var defaultConfig = ConfigurationManager.Configs.FirstOrDefault(c =>
                c.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));

            if (defaultConfig != null)
            {
                foreach (var kvp in activeData)
                {
                    defaultConfig.Config[kvp.Key] = kvp.Value;
                }

                JsonHelper.SaveConfig(
                    defaultConfig.FileName,
                    defaultConfig.Config,
                    new MaaInterfaceSelectAdvancedConverter(false),
                    new MaaInterfaceSelectOptionConverter(false));

                ConfigurationManager.Current = defaultConfig;
            }

            var activeConfig = ConfigurationManager.Configs.FirstOrDefault(c =>
                c.FileName.Equals(activeFileName, StringComparison.OrdinalIgnoreCase));
            if (activeConfig != null)
                ConfigurationManager.Configs.Remove(activeConfig);

            File.Delete(activeFilePath);

            ConfigurationManager.ConfigName = "Default";
            ConfigurationManager.SetDefaultConfig("Default");

            LoggerHelper.Info($"[迁移] 已将 {activeFileName}.json 合并到 config.json，DefaultConfig 已重置为 Default");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[迁移] 合并活跃配置 {activeFileName} 失败", ex);
        }
    }

    /// <summary>
    /// 从 config.json 中提取 Instance.{id}.* scoped keys 到 config/instances/{id}.json
    /// </summary>
    private void MigrateScopedKeysToFiles(string instancesDir)
    {
        var config = ConfigurationManager.Current.Config;
        var prefix = "Instance.";
        var referencedInstanceIds = GetReferencedLegacyInstanceIds();

        // 收集所有 Instance.{id}.{key} 格式的键
        var instanceData = new Dictionary<string, Dictionary<string, object>>();
        var keysToRemove = new List<string>();

        foreach (var kvp in config)
        {
            if (!kvp.Key.StartsWith(prefix)) continue;

            var rest = kvp.Key[prefix.Length..];
            var dotIndex = rest.IndexOf('.');
            if (dotIndex <= 0) continue;

            var instanceId = rest[..dotIndex];
            var key = rest[(dotIndex + 1)..];
            if (!instanceData.ContainsKey(instanceId))
                instanceData[instanceId] = new Dictionary<string, object>();

            // Instance.{id}.Name → InstanceName（存入实例独立文件）
            if (key == "Name")
            {
                instanceData[instanceId][ConfigurationKeys.InstanceName] = kvp.Value;
                keysToRemove.Add(kvp.Key);
                continue;
            }

            // 只迁移实例作用域的 key
            if (!ConfigurationKeys.IsInstanceScoped(key)) continue;

            instanceData[instanceId][key] = kvp.Value;
            keysToRemove.Add(kvp.Key);
        }

        if (instanceData.Count == 0) return;

        LoggerHelper.Info($"[迁移] 从 config.json 中提取 {instanceData.Count} 个实例的 scoped keys 到独立文件...");

        foreach (var (instanceId, data) in instanceData)
        {
            var hasRealInstanceScopedData = data.Keys.Any(key => !string.Equals(key, ConfigurationKeys.InstanceName, StringComparison.Ordinal));
            var instanceFilePath = Path.Combine(instancesDir, $"{instanceId}.json");

            if (!hasRealInstanceScopedData
                && !File.Exists(instanceFilePath)
                && !referencedInstanceIds.Contains(instanceId))
            {
                LoggerHelper.Info($"[迁移] 跳过孤立实例名称键: {instanceId}（仅存在旧版 Name 键，且未被实例列表引用）");
                continue;
            }

            // 如果实例文件已存在，合并（不覆盖已有的）
            var existingData = new Dictionary<string, object>();
            if (File.Exists(instanceFilePath))
            {
                existingData = JsonHelper.LoadJson(instanceFilePath, new Dictionary<string, object>());
            }

            foreach (var kvp in data)
            {
                if (!existingData.ContainsKey(kvp.Key))
                    existingData[kvp.Key] = kvp.Value;
            }

            JsonHelper.SaveJson(
                instanceFilePath,
                existingData,
                new MaaInterfaceSelectAdvancedConverter(false),
                new MaaInterfaceSelectOptionConverter(false));

            LoggerHelper.Info($"[迁移] 已提取实例 {instanceId} 的配置到独立文件");
        }

        // 从 config.json 中移除已迁移的 scoped keys
        foreach (var key in keysToRemove)
        {
            config.Remove(key);
        }

        // 保存清理后的 config.json
        JsonHelper.SaveConfig(
            ConfigurationManager.Current.FileName,
            config,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));

        LoggerHelper.Info("[迁移] 已从 config.json 中清理 scoped keys");
    }

    private static HashSet<string> GetReferencedLegacyInstanceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCsv(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;

            foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(item))
                    ids.Add(item);
            }
        }

        AddCsv(GlobalConfiguration.GetValue(ConfigurationKeys.InstanceList, string.Empty));
        AddCsv(GlobalConfiguration.GetValue(ConfigurationKeys.InstanceOrder, string.Empty));

        var lastActive = GlobalConfiguration.GetValue(ConfigurationKeys.LastActiveInstance, string.Empty);
        if (!string.IsNullOrWhiteSpace(lastActive))
            ids.Add(lastActive);

        ids.Add("default");
        return ids;
    }

    /// <summary>
    /// 将 config.json 中无前缀的 plain instance-scoped keys 迁移到 default 实例文件
    /// 适用于从最早版本（无多实例）升级的场景
    /// </summary>
    private void MigratePlainKeysToDefaultInstance(string instancesDir)
    {
        var config = ConfigurationManager.Current.Config;
        var instanceFilePath = Path.Combine(instancesDir, "default.json");

        var existingData = new Dictionary<string, object>();
        if (File.Exists(instanceFilePath))
        {
            existingData = JsonHelper.LoadJson(instanceFilePath, new Dictionary<string, object>());
        }

        var keysToRemove = new List<string>();
        var migrated = false;

        foreach (var key in ConfigurationKeys.InstanceScopedKeys)
        {
            // 只迁移实例文件中还没有的 key
            if (existingData.ContainsKey(key)) continue;

            if (config.TryGetValue(key, out var value))
            {
                existingData[key] = value;
                keysToRemove.Add(key);
                migrated = true;
            }
        }

        if (!migrated) return;

        LoggerHelper.Info($"[迁移] 将 {keysToRemove.Count} 个 plain keys 从 config.json 迁移到 default 实例文件...");

        JsonHelper.SaveJson(
            instanceFilePath,
            existingData,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));

        // 从 config.json 中移除已迁移的 plain keys
        foreach (var key in keysToRemove)
        {
            config.Remove(key);
        }

        JsonHelper.SaveConfig(
            ConfigurationManager.Current.FileName,
            config,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));

        LoggerHelper.Info("[迁移] plain keys 迁移完成");
    }

    /// <summary>
    /// 将剩余的 mfa_*.json 文件迁移为多实例条目
    /// 只提取实例作用域的 key（TaskItems、连接设置、运行设置、启动设置等），去掉 Instance.{id}. 前缀
    /// </summary>
    private void MigrateRemainingConfigs(string configDir, string instancesDir)
    {
        var legacyFiles = Directory.EnumerateFiles(configDir, "mfa_*.json")
            .Where(f =>
            {
                var fn = Path.GetFileNameWithoutExtension(f);
                return fn != "maa_option";
            })
            .ToList();

        if (legacyFiles.Count == 0) return;

        LoggerHelper.Info($"[迁移] 发现 {legacyFiles.Count} 个旧配置文件，开始迁移到多实例...");

        var migrated = false;

        foreach (var file in legacyFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);

            try
            {
                var legacyData = JsonHelper.LoadJson(file, new Dictionary<string, object>());
                if (legacyData.Count == 0)
                {
                    File.Delete(file);
                    LoggerHelper.Info($"[迁移] 跳过空配置文件 {fileName}，已删除");
                    continue;
                }

                var name = fileName.StartsWith("mfa_") ? fileName["mfa_".Length..] : fileName;
                var instanceId = CreateUniqueId();

                // 只提取实例作用域的 key
                var instanceData = new Dictionary<string, object>();

                // 优先从 Instance.{xxx}.{key} 格式的 scoped key 中提取（去掉前缀）
                var scopedPrefix = "Instance.";
                foreach (var kvp in legacyData)
                {
                    if (!kvp.Key.StartsWith(scopedPrefix)) continue;

                    var rest = kvp.Key[scopedPrefix.Length..];
                    var dotIndex = rest.IndexOf('.');
                    if (dotIndex <= 0) continue;

                    var key = rest[(dotIndex + 1)..];

                    // 只迁移实例作用域的 key，优先使用第一个找到的
                    if (ConfigurationKeys.IsInstanceScoped(key) && !instanceData.ContainsKey(key))
                    {
                        instanceData[key] = kvp.Value;
                    }
                }

                // 再从无前缀的 plain key 中补充（不覆盖已有的 scoped key）
                foreach (var key in ConfigurationKeys.InstanceScopedKeys)
                {
                    if (!instanceData.ContainsKey(key) && legacyData.TryGetValue(key, out var value))
                    {
                        instanceData[key] = value;
                    }
                }

                if (instanceData.Count == 0)
                {
                    File.Delete(file);
                    LoggerHelper.Info($"[迁移] 配置文件 {fileName} 无实例作用域数据，已删除");
                    continue;
                }

                // 将实例名称也存入实例配置
                instanceData[ConfigurationKeys.InstanceName] = name;

                // 保存到实例独立文件（使用自定义转换器确保格式正确）
                var instanceFilePath = Path.Combine(instancesDir, $"{instanceId}.json");
                JsonHelper.SaveJson(
                    instanceFilePath,
                    instanceData,
                    new MaaInterfaceSelectAdvancedConverter(false),
                    new MaaInterfaceSelectOptionConverter(false));

                // 注册实例
                _instanceNames[instanceId] = name;
                if (!_instanceOrder.Contains(instanceId))
                    _instanceOrder.Add(instanceId);

                // 从 ConfigurationManager.Configs 中移除旧配置
                var legacyConfig = ConfigurationManager.Configs.FirstOrDefault(c => c.FileName == fileName);
                if (legacyConfig != null)
                    ConfigurationManager.Configs.Remove(legacyConfig);

                File.Delete(file);
                migrated = true;

                LoggerHelper.Info($"[迁移] 已迁移旧配置 '{name}' → 实例 {instanceId}（提取 {instanceData.Count} 个实例作用域 key）");
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"[迁移] 迁移配置文件 {fileName} 失败", ex);
            }
        }

        if (migrated)
        {
            SaveInstanceConfig();
            LoggerHelper.Info("[迁移] 旧配置迁移完成");
        }
    }

    public void LoadInstanceConfig()
    {
        // 先迁移旧的 mfa_*.json 配置文件
        MigrateLegacyConfigs();

        // 先扫描 instances 目录，获取实际存在的实例文件（这是真实情况）
        var scannedIds = ScanAllInstanceFiles();
        LoggerHelper.Info($"[调试] instances 目录中实际存在的实例文件数量: {scannedIds.Count}, ID列表: [{string.Join(", ", scannedIds)}]");

        // 构造函数创建的临时 default 实例可能因为大小写不一致被写成 Default.json/default.json。
        // 只有明确属于“仅含启动占位数据”的 bootstrap default 文件才允许清理；
        // 否则用户真实使用 default 作为实例 ID 时会被误删。
        var defaultIds = scannedIds
            .Where(id => string.Equals(id, "default", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var bootstrapDefaultIds = defaultIds
            .Where(IsBootstrapDefaultInstance)
            .ToList();
        if (bootstrapDefaultIds.Count > 0
            && scannedIds.Any(id => !string.Equals(id, "default", StringComparison.OrdinalIgnoreCase)))
        {
            LoggerHelper.Info($"[调试] 检测到 {bootstrapDefaultIds.Count} 个临时 default 实例文件，正在删除...");
            foreach (var tempDefaultId in bootstrapDefaultIds)
            {
                try
                {
                    var tempDefaultPath = Path.Combine(InstanceConfiguration.InstancesDir, $"{tempDefaultId}.json");
                    if (File.Exists(tempDefaultPath))
                        File.Delete(tempDefaultPath);
                }
                catch
                {
                    /* ignore */
                }
            }

            scannedIds.RemoveAll(id => bootstrapDefaultIds.Contains(id));
        }
        else if (defaultIds.Count > 0
                 && scannedIds.Any(id => !string.Equals(id, "default", StringComparison.OrdinalIgnoreCase)))
        {
            LoggerHelper.Info("[调试] 检测到存在其他实例的 default 实例文件，但其包含真实配置，已保留");
        }

        if (scannedIds.Count == 1
            && string.Equals(scannedIds[0], "default", StringComparison.OrdinalIgnoreCase)
            && IsBootstrapDefaultInstance(scannedIds[0]))
        {
            LoggerHelper.Info("[调试] 检测到仅包含临时启动数据的 default 实例文件，按首次启动处理");
            try
            {
                var bootstrapDefaultPath = Path.Combine(InstanceConfiguration.InstancesDir, $"{scannedIds[0]}.json");
                if (File.Exists(bootstrapDefaultPath))
                    File.Delete(bootstrapDefaultPath);
            }
            catch
            {
                /* ignore */
            }

            scannedIds.Clear();
        }

        var ids = scannedIds.ToArray();

        // 如果没有任何实例配置，检查是否有 preset，基于 preset 创建初始实例
        if (ids.Length == 0)
        {
            if (MaaProcessor.Interface == null)
            {
                LoggerHelper.Info("MaaProcessor.Interface 为 null，正在读取...");
                MaaProcessor.ReadInterface();
            }

            LoggerHelper.Info($"MaaProcessor.Interface.Preset 数量: {MaaProcessor.Interface?.Preset?.Count ?? 0}");

            // 检查是否存在 preset 定义
            if (MaaProcessor.Interface?.Preset is { Count: > 0 } presets)
            {
                LoggerHelper.Info($"[初始化] 未找到实例配置，将基于 {presets.Count} 个 preset 创建初始实例");

                lock (_lock)
                {
                    // 清理构造函数创建的 default 实例
                    if (_instances.TryGetValue("default", out var defaultProcessor))
                    {
                        defaultProcessor.InstanceConfiguration.DeleteConfigFile();
                        defaultProcessor.Dispose();
                        _instances.Remove("default");
                        _instanceNames.Remove("default");
                        _instanceOrder.Remove("default");
                    }
                    // 为每个 preset 创建一个实例
                    for (int i = 0; i < presets.Count; i++)
                    {
                        var preset = presets[i];
                        var instanceId = CreateUniqueId();
                        var instanceName = LanguageHelper.GetLocalizedDisplayName(
                            preset.Label,
                            preset.Name ?? $"{LangKeys.Config.ToLocalization()} {_instanceOrder.Count + 1}");

                        _instanceNames[instanceId] = instanceName;
                        _instanceOrder.Add(instanceId);
                        _instancePresetIndexes[instanceId] = i; // 记录 preset 索引

                        // 创建实例对象
                        var processor = CreateInstanceInternal(instanceId, setCurrent: false);
                        
                        // 保存实例名称到配置文件
                        processor.InstanceConfiguration.SetValue(ConfigurationKeys.InstanceName, instanceName);
                        ApplyPresetSettings(processor, preset, i);
                        // 先应用 preset，再写入专属配置，避免 preset 命令覆盖模板设置。

                        LoggerHelper.Info($"[初始化] 基于 preset '{preset.Name}' 创建实例 {instanceId} (显示名称: {instanceName})");
                    }


                    // 加载第一个实例作为当前活跃实例
                    if (_instanceOrder.Count > 0)
                    {
                        var firstId = _instanceOrder[0];
                        LoadSingleInstance(firstId);
                        Current = _instances[firstId];
                        SaveInstanceConfig();

                        // 应用对应的 preset 到该实例
                        var firstPreset = presets[0];
                        if (Current.ViewModel != null)
                        {
                            DispatcherHelper.PostOnMainThread(() =>
                            {
                                Current.ViewModel.ApplyPresetCommand.Execute(firstPreset);
                                ApplyPresetSettings(Current, firstPreset, 0);
                            });
                        }

                        // 收集剩余待加载的实例ID
                        _pendingInstanceIds.Clear();
                        for (int i = 1; i < _instanceOrder.Count; i++)
                        {
                            _pendingInstanceIds.Add(_instanceOrder[i]);
                        }

                        _isLazyLoadingComplete = false;
                    }

                    return;
                }
            }

            // 如果没有 preset，继续使用 default 实例，并将其作为真正的初始实例持久化。
            // 否则本次启动虽然看起来已有“默认配置”，但重启后因为磁盘上仍无实例文件，
            // 一旦 interface 后续新增 preset，就会被误判为“首次初始化”，从而被 preset 替换。
            LoggerHelper.Info("没有 preset，将继续使用默认的 default 实例并持久化到配置");

            lock (_lock)
            {
                if (!_instanceOrder.Contains("default"))
                    _instanceOrder.Add("default");

                if (!_instanceNames.ContainsKey("default"))
                    _instanceNames["default"] = $"{LangKeys.Config.ToLocalization()} 1";

                LoadSingleInstance("default");
                Current = _instances["default"];
                Current.InstanceConfiguration.SetValue(ConfigurationKeys.InstanceName, _instanceNames["default"]);
                SaveInstanceConfig();
                _pendingInstanceIds.Clear();
                _isLazyLoadingComplete = true;
            }

            return;
        }
        lock (_lock)
        {
            if (MaaProcessor.Interface == null)
            {
                MaaProcessor.ReadInterface();
            }

            var validIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

            foreach (var staleId in _instanceNames.Keys.Where(id => !validIds.Contains(id)).ToList())
                _instanceNames.Remove(staleId);

            foreach (var staleId in _instancePresetIndexes.Keys.Where(id => !validIds.Contains(id)).ToList())
                _instancePresetIndexes.Remove(staleId);

            // 1. 恢复顺序和名称（不创建实例）
            var orderStr = GlobalConfiguration.GetValue(ConfigurationKeys.InstanceOrder, "");
            _instanceOrder.Clear();
            if (!string.IsNullOrEmpty(orderStr))
            {
                var orders = orderStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var o in orders)
                {
                    if (ids.Contains(o))
                        _instanceOrder.Add(o);
                }
            }

            foreach (var id in ids)
            {
                if (!_instanceOrder.Contains(id))
                    _instanceOrder.Add(id);

                // 优先从实例独立配置文件读取名称
                var name = InstanceConfiguration.ReadValueFromFile(id, ConfigurationKeys.InstanceName);
                var migratedFromGlobal = false;
                if (string.IsNullOrEmpty(name))
                {
                    // 回退：从全局配置的旧格式读取（兼容迁移）
                    var nameKey = string.Format(ConfigurationKeys.InstanceNameTemplate, id);
                    name = GlobalConfiguration.GetValue(nameKey, "");
                    if (!string.IsNullOrEmpty(name))
                        migratedFromGlobal = true;
                }

                if (!string.IsNullOrEmpty(name))
                {
                    _instanceNames[id] = name;
                    // 如果是从全局配置回退读取的，写入实例文件完成迁移
                    if (migratedFromGlobal)
                    {
                        try
                        {
                            var instanceFilePath = Path.Combine(InstanceConfiguration.InstancesDir, $"{id}.json");
                            var data = File.Exists(instanceFilePath)
                                ? JsonHelper.LoadJson(instanceFilePath, new Dictionary<string, object>())
                                : new Dictionary<string, object>();
                            data[ConfigurationKeys.InstanceName] = name;
                            JsonHelper.SaveJson(instanceFilePath, data,
                                new MaaInterfaceSelectAdvancedConverter(false),
                                new MaaInterfaceSelectOptionConverter(false));
                        }
                        catch
                        {
                            /* 迁移失败不影响正常运行 */
                        }
                    }
                }
                else if (!_instanceNames.ContainsKey(id))
                {
                    name = CreateDefaultInstanceName();
                    _instanceNames[id] = name;

                    try
                    {
                        var instanceFilePath = Path.Combine(InstanceConfiguration.InstancesDir, $"{id}.json");
                        var data = File.Exists(instanceFilePath)
                            ? JsonHelper.LoadJson(instanceFilePath, new Dictionary<string, object>())
                            : new Dictionary<string, object>();
                        data[ConfigurationKeys.InstanceName] = name;
                        JsonHelper.SaveJson(instanceFilePath, data,
                            new MaaInterfaceSelectAdvancedConverter(false),
                            new MaaInterfaceSelectOptionConverter(false));
                    }
                    catch
                    {
                        /* 名称回填失败不影响正常运行 */
                    }
                }
            }

            // 先仅同步实例列表/顺序，避免在读取上次活跃实例前把其覆盖掉
            SaveInstanceConfig(false);

            // 2. 清理不在配置中的实例（含磁盘文件，防止构造函数创建的临时实例残留）
            var toRemove = _instances.Keys.Where(k => !validIds.Contains(k)).ToList();
            foreach (var key in toRemove)
            {
                if (_instances.TryGetValue(key, out var p))
                {
                    p.InstanceConfiguration.DeleteConfigFile();
                    p.Dispose();
                    _instances.Remove(key);
                    _instanceNames.Remove(key);
                }
            }

            // 3. 优先加载 ActiveTab 实例
            var lastActive = GlobalConfiguration.GetValue(ConfigurationKeys.LastActiveInstance, "");
            if (string.IsNullOrWhiteSpace(lastActive) || !validIds.Contains(lastActive))
            {
                var lastActiveName = GlobalConfiguration.GetValue(ConfigurationKeys.LastActiveInstanceName, "");
                if (!string.IsNullOrWhiteSpace(lastActiveName))
                {
                    var matchedIdByName = _instanceOrder.FirstOrDefault(id =>
                        validIds.Contains(id)
                        && _instanceNames.TryGetValue(id, out var instanceName)
                        && instanceName.Equals(lastActiveName, StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrWhiteSpace(matchedIdByName))
                        lastActive = matchedIdByName;
                }
            }

            if (string.IsNullOrWhiteSpace(lastActive) || !validIds.Contains(lastActive))
                lastActive = _instanceOrder.FirstOrDefault() ?? ids[0];

            // 支持 -c/-i/--instance 按实例 ID 或名称激活实例。
            if (AppRuntime.RequestedInstance is { } instanceSelector)
            {
                var matchedId = _instanceOrder.FirstOrDefault(id =>
                                    id.Equals(instanceSelector, StringComparison.OrdinalIgnoreCase))
                                ?? _instanceOrder.FirstOrDefault(id =>
                                    _instanceNames.TryGetValue(id, out var name)
                                    && name.Equals(instanceSelector, StringComparison.OrdinalIgnoreCase));
                if (matchedId != null)
                    lastActive = matchedId;
            }

            LoadSingleInstance(lastActive);
            Current = _instances[lastActive];
            SaveInstanceConfig();

            // 4. 收集剩余待加载的实例ID
            _pendingInstanceIds.Clear();
            foreach (var id in _instanceOrder)
            {
                if (id != lastActive && validIds.Contains(id))
                    _pendingInstanceIds.Add(id);
            }

            _isLazyLoadingComplete = false;
        }
    }
    /// <summary>
    /// 扫描 config/instances/ 目录，返回所有实例文件的 ID
    /// </summary>
    private static List<string> ScanAllInstanceFiles()
    {
        var instanceIds = new List<string>();
        if (!Directory.Exists(InstanceConfiguration.InstancesDir))
            return instanceIds;

        foreach (var file in Directory.EnumerateFiles(InstanceConfiguration.InstancesDir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(id))
            {
                if (TryValidateInstanceFile(id, out var reason))
                {
                    instanceIds.Add(id);
                }
                else
                {
                    LoggerHelper.Error($"[实例加载] 已排除异常实例文件: {file}，原因: {reason}");
                }
            }
        }
        return instanceIds;
    }

    private static void ApplyPresetSettings(MaaProcessor processor, MaaInterface.MaaInterfacePreset? preset, int presetIndex = -1)
    {
        var settings = new Dictionary<string, object>();
        foreach (var value in ConfigurationManager.GetTemplateSettings())
            settings[value.Key] = value.Value;
        if (ConfigurationManager.Current.Config.TryGetValue(ConfigurationKeys.EnableLiveView, out var globalLiveView))
            settings[ConfigurationKeys.EnableLiveView] = globalLiveView;
        if (ConfigurationManager.Current.Config.TryGetValue(ConfigurationKeys.LiveViewRefreshRate, out var globalRefreshRate))
            settings[ConfigurationKeys.LiveViewRefreshRate] = globalRefreshRate;

        // preset 专属值优先于全局默认值。
        var presetSettings = presetIndex >= 0
            ? ConfigurationManager.GetPresetSettings(presetIndex, preset?.Name, preset?.Label, preset?.DisplayName)
            : ConfigurationManager.GetPresetSettings(preset?.Name, preset?.Label, preset?.DisplayName);
        foreach (var value in presetSettings)
            settings[value.Key] = value.Value;

        if (settings.Count == 0)
        {
            LoggerHelper.Info($"[初始化] preset '{preset?.Name}' 未找到专属配置");
            return;
        }

        processor.InstanceConfiguration.SetValues(settings);
        LoggerHelper.Info($"[初始化] preset '{preset?.Name}' 已写入 {settings.Count} 项实例配置");

        // TaskQueueViewModel 读取这些值发生在实例构造时；同步刷新可见属性，
        // 使首次按 preset 初始化时立即反映模板配置。
        if (settings.TryGetValue(ConfigurationKeys.EnableLiveView, out var liveView)
            && bool.TryParse(liveView?.ToString(), out var enableLiveView)
            && processor.ViewModel != null)
        {
            processor.ViewModel.EnableLiveView = enableLiveView;
        }

        if (settings.TryGetValue(ConfigurationKeys.LiveViewRefreshRate, out var refreshRate)
            && double.TryParse(refreshRate?.ToString(), out var rate)
            && processor.ViewModel != null)
        {
            processor.ViewModel.LiveViewRefreshRate = rate;
        }
    }

    /// <summary>
    /// 扫描 config/instances/ 目录，返回未在已注册列表中的实例 ID
    /// </summary>
    private static List<string> ScanUnregisteredInstanceFiles(string[] registeredIds)
    {
        var extra = new List<string>();
        if (!Directory.Exists(InstanceConfiguration.InstancesDir))
            return extra;

        var knownSet = new HashSet<string>(registeredIds, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(InstanceConfiguration.InstancesDir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(id) && !knownSet.Contains(id))
            {
                if (TryValidateInstanceFile(id, out var reason))
                {
                    extra.Add(id);
                    LoggerHelper.Info($"[扫描] 发现未注册的实例配置文件: {id}，将自动加载");
                }
                else
                {
                    LoggerHelper.Error($"[实例加载] 已排除异常实例文件: {file}，原因: {reason}");
                }
            }
        }
        return extra;
    }

    /// <summary>
    /// 加载单个实例（内部方法，需在锁内调用或确保线程安全）
    /// </summary>
    private void LoadSingleInstance(string id)
    {
        if (!_instances.ContainsKey(id))
        {
            CreateInstanceInternal(id, setCurrent: false);
        }

        // 防止同一实例被重复初始化（如 LoadInstanceConfig 被调用两次）
        if (_initializedInstances.Add(id))
        {
            _instances[id].InitializeData();
        }
    }

    /// <summary>
    /// 在懒加载阶段后台恢复实例已保存的连接目标，避免首次点进页签时才补恢复。
    /// </summary>
    private static void WarmupConnectionSelection(MaaProcessor? processor)
    {
        var vm = processor?.ViewModel;
        if (vm == null)
            return;

        if (vm.CurrentController == MaaControllerTypes.PlayCover)
        {
            DispatcherHelper.PostOnMainThread(vm.TryReadPlayCoverConfig);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                vm.TryReadAdbDeviceFromConfig(inTask: false, refresh: false, allowAutoDetect: true);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"[懒加载] 预热实例连接目标失败：实例={processor?.InstanceId}，原因={ex.Message}");
            }
        });
    }

    /// <summary>
    /// 懒加载第二阶段：加载当天有定时任务的实例
    /// </summary>
    public void LoadScheduledInstances()
    {
        var timerModel = MFAAvalonia.ViewModels.Other.TimerModel.Instance;
        var scheduledInstanceIds = new HashSet<string>();

        var now = DateTime.Now;
        foreach (var timer in timerModel.Timers)
        {
            if (timer.IsOn
                && timer.ScheduleConfig.ShouldTrigger(now)
                && !string.IsNullOrEmpty(timer.TimerConfig))
            {
                scheduledInstanceIds.Add(timer.TimerConfig);
            }
        }

        lock (_lock)
        {
            var loaded = new List<string>();
            foreach (var id in _pendingInstanceIds)
            {
                if (scheduledInstanceIds.Contains(id))
                {
                    LoadSingleInstance(id);
                    if (_instances.TryGetValue(id, out var processor))
                    {
                        WarmupConnectionSelection(processor);
                    }
                    loaded.Add(id);
                }
            }

            foreach (var id in loaded)
                _pendingInstanceIds.Remove(id);
        }
    }

    /// 懒加载第三阶段：逐个加载剩余实例
    /// </summary>
    public async Task LoadRemainingInstancesAsync()
    {
        // 获取 preset 列表用于自动应用（如果是基于 preset 创建的初始实例）
        var presets = MaaProcessor.Interface?.Preset;

        while (true)
        {
            string? nextId = null;
            int presetIndex = -1;
            MaaProcessor? processor = null;

            lock (_lock)
            {
                if (_pendingInstanceIds.Count == 0)
                {
                    _isLazyLoadingComplete = true;
                    return;
                }

                nextId = _pendingInstanceIds[0];
                _pendingInstanceIds.RemoveAt(0);

                LoadSingleInstance(nextId);
                _instances.TryGetValue(nextId, out processor);

                // 检查是否有对应的 preset 索引
                if (_instancePresetIndexes.TryGetValue(nextId, out var idx))
                {
                    presetIndex = idx;
                }
            }

            // 如果实例有对应的 preset，应用它
            if (presetIndex >= 0 && presets is { Count: > 0 } && presetIndex < presets.Count && processor?.ViewModel != null)
            {
                var preset = presets[presetIndex];
                DispatcherHelper.PostOnMainThread(() =>
                {
                    processor.ViewModel.ApplyPresetCommand.Execute(preset);
                    ApplyPresetSettings(processor, preset, presetIndex);
                });
            }

            WarmupConnectionSelection(processor);

            // 每加载一个实例后等待0.5秒，缓慢加载避免卡UI
            await Task.Delay(500);
        }
    }


    /// <summary>
    /// 检查实例是否已加载
    /// </summary>
    public bool IsInstanceLoaded(string instanceId)
    {
        lock (_lock)
        {
            return _instances.ContainsKey(instanceId);
        }
    }

    /// <summary>
    /// 确保指定实例已加载（按需加载）
    /// </summary>
    public void EnsureInstanceLoaded(string instanceId)
    {
        lock (_lock)
        {
            if (!_instances.ContainsKey(instanceId))
            {
                LoadSingleInstance(instanceId);
                _pendingInstanceIds.Remove(instanceId);
            }
        }
    }

    /// <summary>
    /// 获取所有实例ID和名称（包括尚未加载的），供定时器UI使用
    /// </summary>
    public List<(string Id, string Name)> GetAllInstanceIdsAndNames()
    {
        lock (_lock)
        {
            var result = new List<(string, string)>();
            foreach (var id in _instanceOrder)
            {
                var name = _instanceNames.TryGetValue(id, out var n) ? n : id;
                result.Add((id, name));
            }
            return result;
        }
    }

    public string? ResolveInstanceId(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;

        lock (_lock)
        {
            return _instanceOrder.FirstOrDefault(id =>
                       id.Equals(selector, StringComparison.OrdinalIgnoreCase))
                   ?? _instanceOrder.FirstOrDefault(id =>
                       _instanceNames.TryGetValue(id, out var name)
                       && name.Equals(selector, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 启动懒加载流程（三阶段）
    /// </summary>
    public async System.Threading.Tasks.Task StartLazyLoadingAsync()
    {
        try
        {
            // 阶段2：加载当天有定时任务的实例
            LoadScheduledInstances();
            LoggerHelper.Info("[懒加载] 已加载当天有定时任务的实例");

            // 让UI有时间响应
            await System.Threading.Tasks.Task.Delay(100);

            // 阶段3：逐个加载剩余实例
            await LoadRemainingInstancesAsync();
            LoggerHelper.Info("[懒加载] 所有实例加载完成");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[懒加载] 加载实例失败: {ex}");
        }
    }
}
