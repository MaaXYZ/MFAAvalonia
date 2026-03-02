using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MFAAvalonia.Extensions.MaaFW;

public partial class MaaInterface
{
    /// <summary>
    /// Option 配置项的选项（用于 select/switch 类型）
    /// </summary>
    public partial class MaaInterfaceOptionCase : ObservableObject
    {
        /// <summary>选项唯一标识符</summary>
        [JsonProperty("name")]
        public string? Name { get; set; }

        /// <summary>选项显示名称，支持国际化（以$开头）</summary>
        [JsonProperty("label")]
        public string? Label { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        /// <summary>选项图标文件路径</summary>
        [JsonProperty("icon")]
        public string? Icon { get; set; }

        /// <summary>子配置项列表（选中时显示）</summary>
        [JsonProperty("option")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? Option { get; set; }

        /// <summary>选项激活时的管道覆盖配置</summary>
        [JsonProperty("pipeline_override")]
        public Dictionary<string, JToken>? PipelineOverride { get; set; }

        [ObservableProperty] [JsonIgnore] private string _displayName = string.Empty;

        [ObservableProperty] [JsonIgnore] private bool _hasDescription;

        [ObservableProperty] [JsonIgnore] private string _displayDescription = string.Empty;

        /// <summary>解析后的图标路径（用于 UI 绑定）</summary>
        [ObservableProperty] [JsonIgnore] private string? _resolvedIcon;

        /// <summary>是否有图标</summary>
        [ObservableProperty] [JsonIgnore] private bool _hasIcon;

        /// <summary>
        /// 初始化显示名称并注册语言变化监听
        /// </summary>
        public void InitializeDisplayName()
        {
            UpdateDisplayName();
            LanguageHelper.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e)
        {
            UpdateDisplayName();
        }

        private void UpdateDisplayName()
        {
            DisplayName = LanguageHelper.GetLocalizedDisplayName(Label, Name ?? string.Empty);
            try
            {
                DisplayDescription = LanguageHelper.GetLocalizedString(Description.ResolveContentAsync().Result);
                HasDescription = !string.IsNullOrWhiteSpace(DisplayDescription);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Failed to resolve option case description for '{Name}': {ex.Message}");
                DisplayDescription = string.Empty;
                HasDescription = false;
            }
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (string.IsNullOrWhiteSpace(Icon))
            {
                ResolvedIcon = null;
                HasIcon = false;
                return;
            }

            // 解析图标路径（支持国际化和路径占位符）
            var iconValue = LanguageHelper.GetLocalizedString(Icon);
            ResolvedIcon = ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true);
            HasIcon = !string.IsNullOrWhiteSpace(ResolvedIcon);
        }

        public override string? ToString()
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
            };
            return JsonConvert.SerializeObject(PipelineOverride, settings);
        }
    }

    /// <summary>
    /// Option 配置项的输入字段（用于 input 类型）
    /// </summary>
    public class MaaInterfaceOptionInput
    {
        /// <summary>输入字段唯一标识符</summary>
        [JsonProperty("name")]
        public string? Name { get; set; }

        /// <summary>输入字段显示名称，支持国际化（以$开头）</summary>
        [JsonProperty("label")]
        public string? Label { get; set; }

        // /// <summary>输入字段详细描述，支持 Markdown</summary>
        [JsonProperty("description")]
        public string? Description { get; set; }

        /// <summary>输入字段的默认值</summary>
        [JsonProperty("default")]
        public string? Default { get; set; }

        /// <summary>pipeline_override 中的数据类型: "string", "int", "bool"</summary>
        [JsonProperty("pipeline_type")]
        public string? PipelineType { get; set; }

        /// <summary>正则表达式，用于校验用户输入</summary>
        [JsonProperty("verify")]
        public string? Verify { get; set; }

        /// <summary>正则校验错误时显示的信息，支持国际化</summary>
        [JsonProperty("pattern_msg")]
        public string? PatternMsg { get; set; }

        /// <summary>获取显示名称（优先 Label，否则 Name）</summary>
        [JsonIgnore]
        public string DisplayName => Label ?? Name ?? string.Empty;
    }

    /// <summary>
    /// Option 配置项定义
    /// </summary>
    public partial class MaaInterfaceOption : ObservableObject
    {
        /// <summary>配置项唯一名称标识符</summary>
        [JsonIgnore]
        public string? Name { get; set; } = string.Empty;

        /// <summary>配置项类型: "select"(默认), "input", "switch", "checkbox"</summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// 可选。指定该配置项适用的控制器类型列表。
        /// 数组元素应与 controller 配置中的 name 字段对应。
        /// 若不指定，则表示该配置项在所有控制器类型中都可用。
        /// </summary>
        [JsonProperty("controller")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? Controller { get; set; }

        /// <summary>
        /// 可选。指定该配置项适用的资源包列表。
        /// 数组元素应与 resource 配置中的 name 字段对应。
        /// 若不指定，则表示该配置项在所有资源包中都可用。
        /// </summary>
        [JsonProperty("resource")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? Resource { get; set; }

        /// <summary>配置项显示标签，支持国际化（以$开头）</summary>
        [JsonProperty("label")]
        public string? Label { get; set; }

        /// <summary>配置项详细描述，支持文件路径/URL/Markdown文本</summary>
        [JsonProperty("description")]
        public string? Description { get; set; }

        /// <summary>配置项图标文件路径</summary>
        [JsonProperty("icon")]
        public string? Icon { get; set; }

        /// <summary>可选项列表（用于 select/switch/checkbox 类型）</summary>
        [JsonProperty("cases")]
        public List<MaaInterfaceOptionCase>? Cases { get; set; }

        /// <summary>输入字段列表（用于 input 类型）</summary>
        [JsonProperty("inputs")]
        public List<MaaInterfaceOptionInput>? Inputs { get; set; }

        /// <summary>input 类型的管道覆盖配置（支持 {名称} 变量替换）</summary>
        [JsonProperty("pipeline_override")]
        public Dictionary<string, Dictionary<string, JToken>>? PipelineOverride { get; set; }

        /// <summary>
        /// 默认选项名称。
        /// select/switch 类型为单个字符串；checkbox 类型为字符串数组。
        /// 使用 GenericSingleOrListConverter 统一反序列化为 List&lt;string&gt;。
        /// </summary>
        [JsonProperty("default_case")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? DefaultCases { get; set; }

        /// <summary>获取 select/switch 类型的默认 case 名称（取 DefaultCases 第一项）</summary>
        [JsonIgnore]
        public string? DefaultCase => DefaultCases?.FirstOrDefault();

        /// <summary>文档说明（旧版兼容）</summary>
        [JsonProperty("doc")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? Document { get; set; }

        /// <summary>获取显示名称（优先 Label，否则 Name）</summary>
        [JsonIgnore]
        public string DisplayName => Label ?? Name ?? string.Empty;

        /// <summary>获取配置项类型（默认为 select）</summary>
        [JsonIgnore]
        public string OptionType => Type?.ToLower() ?? "select";

        /// <summary>是否为 select 类型</summary>
        [JsonIgnore]
        public bool IsSelect => OptionType == "select";

        /// <summary>是否为 input 类型</summary>
        [JsonIgnore]
        public bool IsInput => OptionType == "input";

        /// <summary>是否为 switch 类型</summary>
        [JsonIgnore]
        public bool IsSwitch => OptionType == "switch";

        /// <summary>是否为 checkbox 多选类型</summary>
        [JsonIgnore]
        public bool IsCheckbox => OptionType == "checkbox";

        /// <summary>解析后的图标路径（用于 UI 绑定）</summary>
        [ObservableProperty] [JsonIgnore] private string? _resolvedIcon;

        /// <summary>是否有图标</summary>
        [ObservableProperty] [JsonIgnore] private bool _hasIcon;

        /// <summary>
        /// 初始化图标（解析图标路径）
        /// </summary>
        public void InitializeIcon()
        {
            UpdateIcon();
            LanguageHelper.LanguageChanged += OnLanguageChangedForIcon;
        }

        private void OnLanguageChangedForIcon(object? sender, LanguageHelper.LanguageEventArgs e)
        {
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (string.IsNullOrWhiteSpace(Icon))
            {
                ResolvedIcon = null;
                HasIcon = false;
                return;
            }

            // 解析图标路径（支持国际化和路径占位符）
            var iconValue = LanguageHelper.GetLocalizedString(Icon);
            ResolvedIcon = ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true);
            HasIcon = !string.IsNullOrWhiteSpace(ResolvedIcon);
        }

        /// <summary>
        /// 根据用户输入生成处理后的 pipeline override（用于 input 类型）
        /// </summary>
        /// <param name="inputValues">用户输入的值字典（key: 输入字段名，value: 用户输入值）</param>
        /// <returns>处理后的 JSON 字符串</returns>
        public string GenerateProcessedPipeline(Dictionary<string, string> inputValues)
        {
            if (PipelineOverride == null || !IsInput) return "{}";

            // 深拷贝原始数据
            var cloned = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, JToken>>>(
                JsonConvert.SerializeObject(PipelineOverride)
            );

            if (cloned == null) return "{}";

            var typeMap = GetTypeMap();
            var regex = new Regex(@"\{([^{}]+)\}", RegexOptions.Compiled);

            foreach (var preset in cloned.Values)
            {
                foreach (var key in preset.Keys.ToList())
                {
                    var jToken = preset[key];
                    var newToken = ProcessToken(jToken, regex, inputValues, typeMap);
                    if (newToken != null)
                    {
                        preset[key] = newToken;
                    }
                }
            }

            return JsonConvert.SerializeObject(cloned, Formatting.Indented);
        }

        /// <summary>
        /// 获取输入字段的类型映射
        /// </summary>
        private Dictionary<string, Type> GetTypeMap()
        {
            var typeMap = new Dictionary<string, Type>();
            if (Inputs == null) return typeMap;

            foreach (var input in Inputs)
            {
                if (string.IsNullOrEmpty(input.Name)) continue;
                var typeName = (input.PipelineType ?? "string").ToLower();
                typeMap[input.Name] = typeName switch
                {
                    "int" => typeof(long),
                    "bool" => typeof(bool),
                    _ => typeof(string)
                };
            }
            return typeMap;
        }

        /// <summary>
        /// 获取输入字段的默认值映射
        /// </summary>
        private Dictionary<string, string> GetDefaultValues()
        {
            var defaults = new Dictionary<string, string>();
            if (Inputs == null) return defaults;

            foreach (var input in Inputs)
            {
                if (!string.IsNullOrEmpty(input.Name) && !string.IsNullOrEmpty(input.Default))
                {
                    defaults[input.Name] = input.Default;
                }
            }
            return defaults;
        }

        /// <summary>
        /// 处理 JToken 进行变量替换
        /// </summary>
        private JToken? ProcessToken(JToken? token, Regex regex, Dictionary<string, string> inputValues, Dictionary<string, Type> typeMap)
        {
            if (token == null) return null;

            return token.Type switch
            {
                JTokenType.String => ProcessStringToken(token, regex, inputValues, typeMap),
                JTokenType.Array => ProcessArrayToken(token, regex, inputValues, typeMap),
                JTokenType.Object => ProcessObjectToken(token, regex, inputValues, typeMap),
                _ => token
            };
        }

        /// <summary>
        /// 特殊标记，表示用户显式输入的 null 值
        /// </summary>
        public const string ExplicitNullMarker = "\0null";

        private JToken? ProcessStringToken(JToken token, Regex regex, Dictionary<string, string> inputValues, Dictionary<string, Type> typeMap)
        {
            var strVal = token.Value<string>();
            if (string.IsNullOrEmpty(strVal)) return token;

            string? currentPlaceholder = null;
            var defaults = GetDefaultValues();
            bool isExplicitNull = false;

            var newVal = regex.Replace(strVal, match =>
            {
                currentPlaceholder = match.Groups[1].Value;

                // 首先尝试从输入值获取
                if (inputValues.TryGetValue(currentPlaceholder, out var inputStr))
                {
                    // 检查是否是显式 null 标记
                    if (inputStr == ExplicitNullMarker)
                    {
                        isExplicitNull = true;
                        return string.Empty; // 临时返回空字符串，后面会处理
                    }
                    return inputStr;
                }

                // 尝试从默认值获取
                if (defaults.TryGetValue(currentPlaceholder, out var defaultStr))
                {
                    return defaultStr;
                }

                // 保持占位符
                return match.Value;
            });

            // 如果是显式 null，返回 JValue.CreateNull()
            if (isExplicitNull)
            {
                return JValue.CreateNull();
            }

            if (newVal != strVal && currentPlaceholder != null && typeMap.TryGetValue(currentPlaceholder, out var targetType))
            {
                try
                {
                    if (targetType != typeof(string))
                    {
                        var convertedValue = Convert.ChangeType(newVal, targetType);
                        return JToken.FromObject(convertedValue);
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"Option 类型转换失败: {ex.Message}");
                }
            }

            return newVal != strVal ? JToken.FromObject(newVal) : token;
        }

        private JToken ProcessArrayToken(JToken token, Regex regex, Dictionary<string, string> inputValues, Dictionary<string, Type> typeMap)
        {
            var arr = (JArray)token;
            var newArr = new JArray();
            foreach (var item in arr)
            {
                var processedItem = ProcessToken(item, regex, inputValues, typeMap);
                if (processedItem != null)
                {
                    newArr.Add(processedItem);
                }
            }
            return newArr;
        }

        private JToken ProcessObjectToken(JToken token, Regex regex, Dictionary<string, string> inputValues, Dictionary<string, Type> typeMap)
        {
            var obj = (JObject)token;
            var newObj = new JObject();
            foreach (var property in obj.Properties())
            {
                var processedValue = ProcessToken(property.Value, regex, inputValues, typeMap);
                if (processedValue != null)
                {
                    newObj[property.Name] = processedValue;
                }
            }
            return newObj;
        }

        /// <summary>
        /// 验证用户输入是否合法
        /// </summary>
        /// <param name="inputName">输入字段名</param>
        /// <param name="value">用户输入值</param>
        /// <returns>验证结果和错误消息</returns>
        public (bool IsValid, string? ErrorMessage) ValidateInput(string inputName, string value)
        {
            var input = Inputs?.FirstOrDefault(i => i.Name == inputName);
            if (input == null) return (true, null);

            if (string.IsNullOrEmpty(input.Verify))
            {
                // 默认验证：不为空
                if (string.IsNullOrWhiteSpace(value))
                    return (false, input.PatternMsg ?? $"{LanguageHelper.GetLocalizedDisplayName(input.Label, input.Name ?? string.Empty)}");
                return (true, null);
            }

            try
            {
                var regex = new Regex(input.Verify);
                if (!regex.IsMatch(value))
                {
                    return (false, input.PatternMsg ?? $"输入格式不正确");
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"正则表达式验证失败: {ex.Message}");
                return (true, null); // 正则出错时放行
            }

            return (true, null);
        }

        /// <summary>
        /// 验证所有输入字段
        /// </summary>
        public (bool IsValid, string? ErrorMessage) ValidateAllInputs(Dictionary<string, string?>? data)
        {
            if (!IsInput || Inputs == null) return (true, null);

            foreach (var input in Inputs)
            {
                if (string.IsNullOrEmpty(input.Name)) continue;
                var value = data != null && data.TryGetValue(input.Name, out var v) ? v ?? string.Empty : string.Empty;
                var result = ValidateInput(input.Name, value);
                if (!result.IsValid) return result;
            }
            return (true, null);
        }

        public void Merge(MaaInterfaceOption other)
        {
             if (other == null) return;
             if (!string.IsNullOrEmpty(other.Label)) Label = other.Label;
             if (!string.IsNullOrEmpty(other.Description)) Description = other.Description;
             if (!string.IsNullOrEmpty(other.Icon)) Icon = other.Icon;
             if (other.Cases != null) Cases = other.Cases;
             if (other.Inputs != null) Inputs = other.Inputs;
             if (other.PipelineOverride != null) PipelineOverride = other.PipelineOverride;
             if (other.DefaultCases != null && other.DefaultCases.Count > 0) DefaultCases = other.DefaultCases;
             if (other.Controller != null) Controller = other.Controller;
             if (other.Resource != null) Resource = other.Resource;
        }
    }

    public class MaaInterfaceSelectAdvanced
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("data")] public Dictionary<string, string?> Data = new();

        [JsonIgnore] public string PipelineOverride = "{}";

        public override string? ToString()
        {
            return Name ?? string.Empty;
        }
    }

    public class MaaInterfaceSelectOption
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        /// <summary>用于 select/switch 类型的选项索引</summary>
        [JsonProperty("index")]
        public int? Index { get; set; }

        /// <summary>用于 checkbox 类型的多选 case 名称列表</summary>
        [JsonProperty("selected_cases")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? SelectedCases { get; set; }

        /// <summary>用于 input 类型的字段数据（key: 字段名, value: 用户输入值）</summary>
        [JsonProperty("data")]
        public Dictionary<string, string?>? Data { get; set; }

        /// <summary>子配置项列表（当选中某个 case 时会展开的选项）</summary>
        [JsonProperty("sub_options")]
        public List<MaaInterfaceSelectOption>? SubOptions { get; set; }

        /// <summary>input 类型生成的 pipeline override（运行时使用）</summary>
        [JsonIgnore]
        public string PipelineOverride { get; set; } = "{}";

        /// <summary>
        /// 获取显示名称（从对应的 MaaInterfaceOption 复制），用于 UI 显示
        /// </summary>
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(Name))
                    return string.Empty;

                // 从 MaaProcessor.Interface.Option 中查找对应的 MaaInterfaceOption
                if (MaaProcessor.Interface?.Option?.TryGetValue(Name, out var interfaceOption) == true)
                {
                    return interfaceOption.DisplayName;
                }

                return Name;
            }
        }

        public override string? ToString()
        {
            return Name ?? string.Empty;
        }
    }

    public partial class MaaInterfaceTask : ObservableObject
    {
        /// <summary>任务唯一标识符，用作任务ID</summary>
        [JsonProperty("name")] public string? Name;

        /// <summary>任务显示名称，用于在用户界面中展示。支持国际化字符串（以$开头）。如果未设置，则显示 Name 字段的值。</summary>
        [JsonProperty("label")] public string? Label;

        /// <summary>任务显示名称覆盖（仅配置文件使用，不影响接口定义）</summary>
        [JsonProperty("display_name_override")] public string? DisplayNameOverride;

        /// <summary>任务备注（仅配置文件使用，不影响接口定义）</summary>
        [JsonProperty("remark")] public string? Remark;

        /// <summary>任务入口，为 pipeline 中 Task 的名称</summary>
        [JsonProperty("entry")] public string? Entry;

        /// <summary>是否默认选中该任务。Client在初始化时会根据该值决定是否默认勾选该任务。</summary>
        [JsonProperty("default_check",
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include)]
        public bool? Check = false;

        /// <summary>任务详细描述信息，帮助用户理解任务功能。支持文件路径、URL或直接文本，内容支持Markdown格式。</summary>
        [JsonProperty("description")] public string? Description;

        /// <summary>任务图标文件路径</summary>
        [JsonProperty("icon")] public string? Icon;

        /// <summary>
        /// 可选。指定该任务支持的资源包列表。
        /// 数组元素应与 resource 配置中的 name 字段对应。
        /// 若不指定，则表示该任务在所有资源包中都可用。
        /// 当用户选择了某个资源包时，只有支持该资源包的任务才会显示在用户界面中供选择。
        /// 这允许为不同资源包提供专门的任务配置，比如活动任务只在特定资源包中可用。
        /// </summary>
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("resource")]
        public List<string>? Resource;

        /// <summary>
        /// 可选。指定该任务支持的控制器类型列表。
        /// 数组元素应与 controller 配置中的 name 字段对应。
        /// 若不指定，则表示该任务在所有控制器类型中都可用。
        /// 当用户选择了某个控制器时，只有支持该控制器的任务才会显示在用户界面中供选择。
        /// 这允许为不同控制器类型提供专门的任务配置，比如某些任务只适用于 Adb 控制器，某些任务只适用于 Win32 控制器。
        /// </summary>
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("controller")]
        public List<string>? Controller;

        /// <summary>文档说明（旧版兼容）</summary>
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("doc")]
        public List<string>? Document;

        [JsonProperty("repeatable")] public bool? Repeatable;
        [JsonProperty("repeat_count")] public int? RepeatCount;
        [JsonProperty("advanced")] public List<MaaInterfaceSelectAdvanced>? Advanced;
        [JsonProperty("option")] public List<MaaInterfaceSelectOption>? Option;

        [JsonProperty("pipeline_override")] public Dictionary<string, JToken>? PipelineOverride;

        /// <summary>获取显示名称（优先 Label，否则 Name）</summary>
        [JsonIgnore]
        public string DisplayName => Label ?? Name ?? string.Empty;

        /// <summary>解析后的图标路径（用于 UI 绑定）</summary>
        [ObservableProperty] [JsonIgnore] private string? _resolvedIcon;

        /// <summary>是否有图标</summary>
        [ObservableProperty] [JsonIgnore] private bool _hasIcon;

        /// <summary>
        /// 初始化图标（解析图标路径）
        /// </summary>
        public void InitializeIcon()
        {
            UpdateIcon();
            LanguageHelper.LanguageChanged += OnLanguageChangedForIcon;
        }

        private void OnLanguageChangedForIcon(object? sender, LanguageHelper.LanguageEventArgs e)
        {
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (string.IsNullOrWhiteSpace(Icon))
            {
                ResolvedIcon = null;
                HasIcon = false;
                return;
            }

            // 解析图标路径（支持国际化和路径占位符）
            var iconValue = LanguageHelper.GetLocalizedString(Icon);
            ResolvedIcon = ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true);
            HasIcon = !string.IsNullOrWhiteSpace(ResolvedIcon);
        }

        public override string ToString()
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
            };

            return JsonConvert.SerializeObject(this, settings);
        }

        /// <summary>
        /// Creates a deep copy of the current <see cref="MaaInterfaceTask"/> instance.
        /// </summary>
        /// <returns>A new <see cref="MaaInterfaceTask"/> instance that is a deep copy of the current instance.</returns>
        public MaaInterfaceTask Clone()
        {
            return JsonConvert.DeserializeObject<MaaInterfaceTask>(ToString()) ?? new MaaInterfaceTask();
        }

        public void Merge(MaaInterfaceTask other)
        {
             if (other == null) return;
             // Name is essentially the key, so we don't need to merge it if we found it by key
             if (!string.IsNullOrEmpty(other.Label)) Label = other.Label;
             if (!string.IsNullOrEmpty(other.DisplayNameOverride)) DisplayNameOverride = other.DisplayNameOverride;
             if (!string.IsNullOrEmpty(other.Remark)) Remark = other.Remark;
             if (!string.IsNullOrEmpty(other.Entry)) Entry = other.Entry;
             if (other.Check != null) Check = other.Check;
             if (!string.IsNullOrEmpty(other.Description)) Description = other.Description;
             if (!string.IsNullOrEmpty(other.Icon)) Icon = other.Icon;
             if (other.Resource != null) Resource = other.Resource;
             if (other.Controller != null) Controller = other.Controller;
             if (other.Document != null) Document = other.Document;
             if (other.Repeatable != null) Repeatable = other.Repeatable;
             if (other.RepeatCount != null) RepeatCount = other.RepeatCount;
             if (other.Advanced != null) Advanced = other.Advanced;
             if (other.Option != null) Option = other.Option;
             if (other.PipelineOverride != null) PipelineOverride = other.PipelineOverride;
        }
    }

    public partial class MaaInterfaceResource : ObservableObject
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("label")]
        public string? Label { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        /// <summary>资源图标文件路径，相对于项目根目录。支持国际化（以$开头）</summary>
        [JsonProperty("icon")]
        public string? Icon { get; set; }

        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        [JsonProperty("path")]
        public List<string>? Path { get; set; }

        /// <summary>
        /// 可选。指定该资源包支持的控制器类型列表。
        /// 数组元素应与 controller 配置中的 name 字段对应。
        /// 若不指定，则表示支持所有控制器类型。
        /// </summary>
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        [JsonProperty("controller")]
        public List<string>? Controller { get; set; }

        /// <summary>
        /// 资源包的全局选项配置。
        /// 这些选项会在任务列表中显示为一个特殊的任务项（位于最前面），
        /// checkbox 默认选中且不可更改。
        /// 该项不参与任务执行，但其 option 生成的参数会参与到所有任务的 MaaToken merge 中，
        /// 起到全局参数传递的作用。
        /// </summary>
        [JsonProperty("option")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? Option { get; set; }

        /// <summary>
        /// 运行时使用的选项配置（与 MaaInterfaceTask.Option 类似）
        /// </summary>
        [JsonIgnore]
        public List<MaaInterfaceSelectOption>? SelectOptions { get; set; }

        [JsonIgnore]
        public List<string>? ResolvedPath { get; set; }

        [ObservableProperty] [JsonIgnore] private string _displayName = string.Empty;

        [ObservableProperty] [JsonIgnore] private bool _hasDescription = false;

        [ObservableProperty] [JsonIgnore] private string _displayDescription = string.Empty;

        /// <summary>解析后的图标路径（用于 UI 绑定）</summary>
        [ObservableProperty] [JsonIgnore] private string? _resolvedIcon;

        /// <summary>是否有图标</summary>
        [ObservableProperty] [JsonIgnore] private bool _hasIcon;

        /// <summary>
        /// 初始化显示名称并注册语言变化监听
        /// </summary>
        public void InitializeDisplayName()
        {
            UpdateDisplayName();
            LanguageHelper.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e)
        {
            UpdateDisplayName();
        }

        private void UpdateDisplayName()
        {
            DisplayName = LanguageHelper.GetLocalizedDisplayName(Label, Name ?? string.Empty);
            try
            {
                DisplayDescription = LanguageHelper.GetLocalizedString(Description.ResolveContentAsync().Result);
                HasDescription = !string.IsNullOrWhiteSpace(DisplayDescription);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Failed to resolve resource description for '{Name}': {ex.Message}");
                DisplayDescription = string.Empty;
                HasDescription = false;
            }
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (string.IsNullOrWhiteSpace(Icon))
            {
                ResolvedIcon = null;
                HasIcon = false;
                return;
            }

            // 解析图标路径（支持国际化和路径占位符）
            var iconValue = LanguageHelper.GetLocalizedString(Icon);
            ResolvedIcon = ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true);
            HasIcon = !string.IsNullOrWhiteSpace(ResolvedIcon);
        }
    }

    public class MaaResourceVersion
    {
        [JsonProperty("name")]
        public string? Name { get; set; }
        [JsonProperty("version")]
        public string? Version { get; set; }
        [JsonProperty("url")]
        public string? Url { get; set; }


        public override string? ToString()
        {
            return Version ?? string.Empty;
        }
    }

    public class MaaResourceControllerAdb
    {
        [JsonProperty("input")]
        public long? Input { get; set; }
        [JsonProperty("screencap")]
        public long? ScreenCap { get; set; }
        [JsonProperty("config")]
        public object? Adb { get; set; }
    }

    public class MaaResourceControllerPlayCover
    {
        [JsonProperty("uuid")]
        public string? Uuid { get; set; }
    }

    public class MaaResourceControllerWin32
    {
        [JsonProperty("class_regex")]
        public string? ClassRegex { get; set; }
        [JsonProperty("window_regex")]
        public string? WindowRegex { get; set; }
        [JsonProperty("input")]
        public object? Input { get; set; }
        [JsonProperty("mouse")]
        public object? Mouse { get; set; }
        [JsonProperty("keyboard")]
        public object? Keyboard { get; set; }
        [JsonProperty("screencap")]
        public object? ScreenCap { get; set; }
    }

    /// <summary>
    /// Gamepad 控制器的具体配置（仅 Windows）。
    /// 用于创建虚拟游戏手柄进行游戏控制。需要安装 ViGEm Bus Driver。
    /// </summary>
    public class MaaResourceControllerGamepad
    {
        /// <summary>
        /// 可选。Win32 控制器搜索窗口类名使用的正则表达式。
        /// </summary>
        [JsonProperty("class_regex")]
        public string? ClassRegex { get; set; }

        /// <summary>
        /// 可选。Win32 控制器搜索窗口标题使用的正则表达式。
        /// </summary>
        [JsonProperty("window_regex")]
        public string? WindowRegex { get; set; }

        /// <summary>
        /// 可选。虚拟手柄类型，取值为 Xbox360、DualShock4（或 DS4）。不提供则默认使用 Xbox360。
        /// </summary>
        [JsonProperty("gamepad_type")]
        public string? GamepadType { get; set; }

        /// <summary>
        /// 可选。截图方式，不提供则使用默认。仅当配置了窗口正则时有效。
        /// </summary>
        [JsonProperty("screencap")]
        public object? ScreenCap { get; set; }
    }

    public class MaaInterfaceAgent
    {
        [JsonProperty("child_exec")]
        public string? ChildExec { get; set; }
        [JsonProperty("child_args")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? ChildArgs { get; set; }
        [JsonProperty("identifier")]
        public string? Identifier { get; set; }

        [JsonProperty("timeout")]
        public long? Timeout { get; set; }
    }

    public partial class MaaResourceController : ObservableObject
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("label")]
        public string? Label { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("type")]
        public string? Type { get; set; }

        [JsonProperty("icon")]
        public string? Icon { get; set; }

        [JsonProperty("display_short_side")]
        public long? DisplayShortSide { get; set; }

        [JsonProperty("display_long_side")]
        public long? DisplayLongSide { get; set; }

        [JsonProperty("display_raw")]
        public bool? DisplayRaw { get; set; }

        [JsonProperty("permission_required")]
        public bool? PermissionRequired { get; set; }
        
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        [JsonProperty("attach_resource_path")]
        public List<string>? AttachResourcePath { get; set; }

        /// <summary>
        /// 可选。控制器级的选项配置，为一个字符串数组，数组元素应与外层 option 配置中的键名对应。
        /// 该选项生成的参数会参与到所有使用该控制器的任务的 pipeline override 中。
        /// </summary>
        [JsonProperty("option")]
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
        public List<string>? Option { get; set; }

        /// <summary>运行时使用的选项配置（与 MaaInterfaceTask.Option 类似）</summary>
        [JsonIgnore]
        public List<MaaInterface.MaaInterfaceSelectOption>? SelectOptions { get; set; }

        [JsonProperty("adb")]
        public MaaResourceControllerAdb? Adb { get; set; }
        [JsonProperty("win32")]
        public MaaResourceControllerWin32? Win32 { get; set; }
        [JsonProperty("playcover")]
        public MaaResourceControllerPlayCover? PlayCover { get; set; }
        [JsonProperty("gamepad")]
        public MaaResourceControllerGamepad? Gamepad { get; set; }

        /// <summary>显示名称（用于 UI 绑定）</summary>
        [ObservableProperty] [JsonIgnore] private string _displayName = string.Empty;

        [ObservableProperty] [JsonIgnore] private bool _hasDescription;

        [ObservableProperty] [JsonIgnore] private string _displayDescription = string.Empty;

        /// <summary>解析后的图标路径（用于 UI 绑定）</summary>
        [ObservableProperty] [JsonIgnore] private string? _resolvedIcon;

        /// <summary>是否有图标</summary>
        [ObservableProperty] [JsonIgnore] private bool _hasIcon;

        /// <summary>控制器类型枚举值</summary>
        [JsonIgnore]
        public MaaControllerTypes ControllerType => Type.ToMaaControllerTypes();

        /// <summary>
        /// 初始化显示名称并注册语言变化监听
        /// </summary>
        public void InitializeDisplayName()
        {
            UpdateDisplayName();
            LanguageHelper.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e)
        {
            UpdateDisplayName();
        }

        private void UpdateDisplayName()
        {
            // 如果有Label，使用Label的国际化；否则使用Name或默认的控制器类型名称
            if (!string.IsNullOrWhiteSpace(Label))
            {
                DisplayName = LanguageHelper.GetLocalizedDisplayName(Label, Name ?? string.Empty);
            }
            else if (!string.IsNullOrWhiteSpace(Name))
            {
                DisplayName = Name;
            }
            else
            {
                // 使用默认的控制器类型名称
                DisplayName = ControllerType.ToResourceKey().ToLocalization();
            }

            try
            {
                DisplayDescription = LanguageHelper.GetLocalizedString(Description.ResolveContentAsync().Result);
                HasDescription = !string.IsNullOrWhiteSpace(DisplayDescription);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Failed to resolve controller description for '{Name}': {ex.Message}");
                DisplayDescription = string.Empty;
                HasDescription = false;
            }

            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (string.IsNullOrWhiteSpace(Icon))
            {
                ResolvedIcon = null;
                HasIcon = false;
                return;
            }

            // 解析图标路径（支持国际化和路径占位符）
            var iconValue = LanguageHelper.GetLocalizedString(Icon);
            ResolvedIcon = ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true);
            HasIcon = !string.IsNullOrWhiteSpace(ResolvedIcon);
        }
    }


    /// <summary>
    /// 预设任务配置（preset.task 中的单个任务项）
    /// </summary>
    public class MaaInterfacePresetTask
    {
        /// <summary>与顶层 task[].name 对应的任务名称</summary>
        [JsonProperty("name")]
        public string? Name { get; set; }

        /// <summary>该任务在预设中是否勾选，默认 true</summary>
        [JsonProperty("enabled")]
        public bool? Enabled { get; set; }

        /// <summary>
        /// 该任务各配置项的预设值。
        /// key 为顶层 option 中的键名，value 类型取决于 option.type：
        /// - select/switch: string（case.name）
        /// - checkbox: string[]（case.name 数组）
        /// - input: Dictionary&lt;string, string&gt;（输入字段 name → 值）
        /// </summary>
        [JsonProperty("option")]
        public Dictionary<string, JToken>? Option { get; set; }
    }

    /// <summary>
    /// 预设配置（一套预定义的任务勾选状态与选项值的快照）
    /// </summary>
    public partial class MaaInterfacePreset : ObservableObject, IDisposable
    {
        /// <summary>唯一标识符，用作预设 ID</summary>
        [JsonProperty("name")]
        public string? Name { get; set; }

        /// <summary>显示名称，支持国际化（以$开头）</summary>
        [JsonProperty("label")]
        public string? Label { get; set; }

        /// <summary>预设详细描述，支持文件路径/URL/Markdown文本，支持国际化</summary>
        [JsonProperty("description")]
        public string? Description { get; set; }

        /// <summary>预设图标文件路径，相对于项目根目录，支持国际化</summary>
        [JsonProperty("icon")]
        public string? Icon { get; set; }

        /// <summary>预设包含的任务配置列表</summary>
        [JsonProperty("task")]
        public List<MaaInterfacePresetTask>? Task { get; set; }

        [ObservableProperty] [JsonIgnore] private string _displayName = string.Empty;

        [ObservableProperty] [JsonIgnore] private bool _hasDescription;

        [ObservableProperty] [JsonIgnore] private string _displayDescription = string.Empty;

        [ObservableProperty] [JsonIgnore] private string? _resolvedIcon;

        [ObservableProperty] [JsonIgnore] private bool _hasIcon;

        public void InitializeDisplayName()
        {
            UpdateDisplayName();
            LanguageHelper.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e) => UpdateDisplayName();

        private void UpdateDisplayName()
        {
            DisplayName = LanguageHelper.GetLocalizedDisplayName(Label, Name ?? string.Empty);
            try
            {
                DisplayDescription = LanguageHelper.GetLocalizedString(Description.ResolveContentAsync().Result);
                HasDescription = !string.IsNullOrWhiteSpace(DisplayDescription);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Failed to resolve preset description for '{Name}': {ex.Message}");
                DisplayDescription = string.Empty;
                HasDescription = false;
            }
            if (!string.IsNullOrWhiteSpace(Icon))
            {
                var iconValue = LanguageHelper.GetLocalizedString(Icon);
                ResolvedIcon = ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true);
                HasIcon = !string.IsNullOrWhiteSpace(ResolvedIcon);
            }
            else
            {
                ResolvedIcon = null;
                HasIcon = false;
            }
        }

        public void Dispose()
        {
            LanguageHelper.LanguageChanged -= OnLanguageChanged;
        }
    }

    [JsonProperty("interface_version")]
    public int? InterfaceVersion { get; set; }

    [JsonProperty("import")]
    [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
    public List<string>? Import { get; set; }

    /// <summary>
    /// 多语言支持配置，键为语言代码，值为对应的翻译文件路径（相对于 interface.json 同目录）
    /// 示例: { "zh_cn": "interface_zh.json", "en_us": "interface_en.json" }
    /// </summary>
    [JsonProperty("languages")]
    public Dictionary<string, string>? Languages { get; set; }

    [JsonProperty("mirrorchyan_rid")]
    public string? RID { get; set; }

    [JsonProperty("mirrorchyan_multiplatform")]
    public bool? Multiplatform { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("version")]
    public string? Version { get; set; }

    [JsonProperty("__mfa_max_version")]
    public string? MFAMaxVersion { get; set; }

    [JsonProperty("__mfa_min_version")]
    public string? MFAMinVersion { get; set; }

    [JsonProperty("welcome")]
    public string? Welcome { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("github")]
    public string? Github { get; set; }

    [JsonProperty("url")]
    public string? Url { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("custom_title")]
    public string? CustomTitle { get; set; }

    [JsonProperty("default_controller")]
    public string? DefaultController { get; set; }

    [JsonProperty("lock_controller")]
    public bool LockController { get; set; }

    [JsonProperty("controller")]
    public List<MaaResourceController>? Controller { get; set; }
    [JsonProperty("resource")]
    public List<MaaInterfaceResource>? Resource { get; set; }
    [JsonProperty("task")]
    public List<MaaInterfaceTask>? Task { get; set; }

    [JsonProperty("agent")]
    [JsonConverter(typeof(GenericSingleOrListConverter<MaaInterfaceAgent>))]
    public List<MaaInterfaceAgent>? Agent { get; set; }

    [JsonProperty("advanced")]
    public Dictionary<string, MaaInterfaceAdvancedOption>? Advanced { get; set; }

    [JsonProperty("option")]
    public Dictionary<string, MaaInterfaceOption>? Option { get; set; }

    /// <summary>
    /// 全局选项配置，为一个字符串数组，数组元素应与 option 配置中的键名对应。
    /// 该选项生成的参数会参与到所有任务的 pipeline override 中，优先级最低。
    /// 覆盖顺序：global_option &lt; resource.option &lt; controller.option &lt; task.option
    /// </summary>
    [JsonProperty("global_option")]
    [JsonConverter(typeof(GenericSingleOrListConverter<string>))]
    public List<string>? GlobalOption { get; set; }

    /// <summary>运行时使用的全局选项配置（与 MaaInterfaceTask.Option 类似）</summary>
    [JsonIgnore]
    public List<MaaInterfaceSelectOption>? GlobalSelectOptions { get; set; }

    /// <summary>
    /// 预设配置，为一个对象数组。每个预设是一套预定义的任务勾选状态与选项值的快照。
    /// </summary>
    [JsonProperty("preset")]
    public List<MaaInterfacePreset>? Preset { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object> AdditionalData { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, MaaInterfaceResource> Resources { get; } = new();

    /// <summary>联系方式信息，显示在"关于"页面。支持文件路径、URL或直接文本，内容支持Markdown格式。支持国际化（以$开头）</summary>
    [JsonProperty("contact")]
    public string? Contact { get; set; }

    /// <summary>项目描述信息，显示在"关于"页面。支持文件路径、URL或直接文本，内容支持Markdown格式。支持国际化（以$开头）</summary>
    [JsonProperty("description")]
    public string? Description { get; set; }

    /// <summary>项目许可证信息，显示在"关于"页面。支持文件路径、URL或直接文本，内容支持Markdown格式。支持国际化（以$开头）</summary>
    [JsonProperty("license")]
    public string? License { get; set; }


    /// <summary>
    /// 替换单个字符串中的 {PROJECT_DIR} 占位符，并标准化为当前系统的路径格式
    /// </summary>
    /// <param name="input">待处理的字符串（可能包含路径片段）</param>
    /// <param name="replacement">占位符替换值（项目目录路径）</param>
    /// <param name="checkIfPath">是否检查 input 是否为路径。
    /// - 为 false（默认）：无 {PROJECT_DIR} 时直接 Path.Combine 拼接
    /// - 为 true：无 {PROJECT_DIR} 时，仅当 input 是路径才拼接，否则原样返回</param>
    /// <returns>替换后并标准化路径格式的字符串</returns>
    public static string? ReplacePlaceholder(string? input, string? replacement, bool checkIfPath = false)
    {
        // 处理输入为空的情况，保持原有行为
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // 处理替换值为 null 的情况（避免 Replace 抛出空引用异常）
        string safeReplacement = replacement ?? string.Empty;

        string result;
        // 步骤1：检查是否包含 {PROJECT_DIR} 占位符
        if (input.Contains("{PROJECT_DIR}"))
        {
            // 有占位符，直接替换
            result = input.Replace("{PROJECT_DIR}", safeReplacement);
        }
        else
        {
            var path = Path.Combine(safeReplacement, input);
            // 无占位符
            if (checkIfPath)
            {
                if (File.Exists(path))
                    return path;
                result = input;
            }
            else
            {
                // 未开启路径检查：直接拼接
                result = path;
            }
        }

        // 步骤2：标准化路径分隔符（适配当前操作系统，不检查文件是否存在）
        return NormalizePathSeparators(result);
    }

    /// <summary>
    /// 判断字符串是否看起来像路径（相对路径或绝对路径）
    /// </summary>
    private static bool IsPathLike(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // 包含路径分隔符
        if (input.Contains('/') || input.Contains('\\'))
            return true;

        // 绝对路径（Windows 盘符 或 Unix 根目录）
        if (Path.IsPathRooted(input))
            return true;

        // 以 . 或 .. 开头的相对路径
        if (input.StartsWith("./") || input.StartsWith(".\\") || input.StartsWith("../") || input.StartsWith("..\\") || input == "." || input == "..")
            return true;

        // 包含常见的文件扩展名
        var ext = Path.GetExtension(input);
        if (!string.IsNullOrEmpty(ext))
            return true;

        return false;
    }

    /// <summary>
    /// 替换字符串列表中所有元素的 {PROJECT_DIR} 占位符，并标准化路径格式
    /// </summary>
    /// <param name="inputs">待处理的字符串列表（可能包含路径片段）</param>
    /// <param name="replacement">占位符替换值（项目目录路径）</param>
    /// <param name="checkIfPath">是否检查每个元素是否为路径（详见单个字符串版本的说明）</param>
    /// <returns>处理后的字符串列表</returns>
    public static List<string> ReplacePlaceholder(IEnumerable<string>? inputs, string? replacement, bool checkIfPath = false)
    {
        if (inputs == null)
            return new List<string>();

        // 复用单个字符串的处理逻辑（自动包含占位符替换和路径标准化）
        return inputs.ToList().ConvertAll(input => ReplacePlaceholder(input, replacement, checkIfPath)!);
    }

    /// <summary>
    /// 辅助方法：标准化路径分隔符（核心逻辑）
    /// - 将所有 / 和 \ 统一替换为当前系统的路径分隔符
    /// - 移除连续的重复分隔符（避免 "a//b" 这类无效格式）
    /// - 不改变路径结构、不补全路径、不检查文件存在性
    /// </summary>
    private static string NormalizePathSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // 1. 获取当前系统的路径分隔符（Windows 是 \，Linux/macOS 是 /）
        char targetSeparator = Path.DirectorySeparatorChar;

        // 2. 将所有 / 和 \ 统一替换为目标分隔符
        string normalized = path.Replace('/', targetSeparator).Replace('\\', targetSeparator);

        // 3. 移除连续的重复分隔符（例如 "a//b\\\c" → "a/b/c" 或 "a\b\c"）
        StringBuilder sb = new StringBuilder(normalized.Length);
        char lastChar = '\0';
        foreach (char c in normalized)
        {
            // 跳过与上一个字符相同的分隔符
            if (c == targetSeparator && lastChar == targetSeparator)
                continue;

            sb.Append(c);
            lastChar = c;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 将另一个 MaaInterface 合并到当前实例中 (传入的 other 实例优先级更高，会覆盖当前实例的同名字段)
    /// </summary>
    public void Merge(MaaInterface? other)
    {
        if (other == null) return;

        if (other.InterfaceVersion != null) InterfaceVersion = other.InterfaceVersion;
        if (!string.IsNullOrEmpty(other.RID)) RID = other.RID;
        if (other.Multiplatform != null) Multiplatform = other.Multiplatform;
        if (!string.IsNullOrEmpty(other.Name)) Name = other.Name;
        if (!string.IsNullOrEmpty(other.Label)) Label = other.Label;
        if (!string.IsNullOrEmpty(other.Version)) Version = other.Version;
        if (!string.IsNullOrEmpty(other.MFAMaxVersion)) MFAMaxVersion = other.MFAMaxVersion;
        if (!string.IsNullOrEmpty(other.MFAMinVersion)) MFAMinVersion = other.MFAMinVersion;
        if (!string.IsNullOrEmpty(other.Welcome)) Welcome = other.Welcome;
        if (!string.IsNullOrEmpty(other.Message)) Message = other.Message;
        if (!string.IsNullOrEmpty(other.Github)) Github = other.Github;
        if (!string.IsNullOrEmpty(other.Url)) Url = other.Url;
        if (!string.IsNullOrEmpty(other.Title)) Title = other.Title;
        if (!string.IsNullOrEmpty(other.CustomTitle)) CustomTitle = other.CustomTitle;
        if (!string.IsNullOrEmpty(other.DefaultController)) DefaultController = other.DefaultController;
        if (other.LockController) LockController = other.LockController;
        if (!string.IsNullOrEmpty(other.Contact)) Contact = other.Contact;
        if (!string.IsNullOrEmpty(other.Description)) Description = other.Description;
        if (!string.IsNullOrEmpty(other.License)) License = other.License;
        if (other.Agent is { Count: > 0 }) Agent = other.Agent;

        Advanced = MergeDictionaries(Advanced, other.Advanced);
        Option = MergeDictionaries(Option, other.Option);
        Languages = MergeDictionaries(Languages, other.Languages);
        
        // Merge AdditionalData
        if (other.AdditionalData != null)
        {
            foreach (var kvp in other.AdditionalData)
            {
                AdditionalData[kvp.Key] = kvp.Value;
            }
        }

        Controller = MergeLists(Controller, other.Controller, c => c.Name);
        Resource = MergeLists(Resource, other.Resource, r => r.Name);
        Task = MergeTasks(Task, other.Task);
        Preset = MergeLists(Preset, other.Preset, p => p.Name);
        if (other.GlobalOption is { Count: > 0 }) GlobalOption = other.GlobalOption;
    }

    private static Dictionary<TK, TV>? MergeDictionaries<TK, TV>(Dictionary<TK, TV>? first, Dictionary<TK, TV>? second) where TK : notnull
    {
        if (first == null && second == null) return null;
        if (first == null) return new Dictionary<TK, TV>(second!);
        if (second == null) return new Dictionary<TK, TV>(first);

        var result = new Dictionary<TK, TV>(first);
        foreach (var kvp in second)
        {
            if (typeof(TV) == typeof(MaaInterfaceOption) && result.TryGetValue(kvp.Key, out var existingVal) && existingVal is MaaInterfaceOption opt1 && kvp.Value is MaaInterfaceOption opt2)
            {
                opt1.Merge(opt2);
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    private static List<MaaInterfaceTask>? MergeTasks(List<MaaInterfaceTask>? first, List<MaaInterfaceTask>? second)
    {
        if (first == null && second == null) return null;
        if (first == null) return new List<MaaInterfaceTask>(second!);
        if (second == null) return new List<MaaInterfaceTask>(first);

        // 深拷贝一份 first
        var result = first.Select(t => t.Clone()).ToList();

        foreach (var item in second)
        {
            var key = item.Name;
            if (!string.IsNullOrEmpty(key))
            {
                var existingItem = result.FirstOrDefault(i => i.Name == key);
                if (existingItem != null)
                {
                    existingItem.Merge(item);
                }
                else
                {
                    result.Add(item);
                }
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static List<T>? MergeLists<T>(List<T>? first, List<T>? second, Func<T, string?> keySelector)
    {
        if (first == null && second == null) return null;
        if (first == null) return new List<T>(second!);
        if (second == null) return new List<T>(first);

        var result = new List<T>(first);
        
        foreach (var item in second)
        {
            var key = keySelector(item);
            if (!string.IsNullOrEmpty(key))
            {
                var index = result.FindIndex(i => keySelector(i) == key);
                if (index >= 0)
                {
                    result[index] = item;
                }
                else
                {
                    result.Add(item);
                }
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    public override string? ToString()
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        return JsonConvert.SerializeObject(this, settings);
    }
}
