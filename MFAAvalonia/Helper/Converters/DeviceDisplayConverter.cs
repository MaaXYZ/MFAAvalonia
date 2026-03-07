using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Templates;
using FluentAvalonia.UI.Controls;
using MaaFramework.Binding;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MFAAvalonia.Helper.Converters;

public class DeviceDisplayConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AdbDeviceInfo device)
        {
            var name = device.Name;
            var index = GetFirstEmulatorIndex(device.Config);
            name = name.Contains("-") ? name.Split("-")[0] : name;
            return index == -1 ? $"{name} ({device.AdbSerial})" : $"{name} ({device.AdbSerial}) [{index}]";
        }
        if (value is DesktopWindowInfo info)
        {
            return info.Name;
        }
        if (value is EmptyDevicePlaceholder placeholder)
        {
            return placeholder.SelectionText;
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo cultureInfo) => throw new NotSupportedException();

    /// <summary>
    /// 从模拟器配置JSON中获取第一个有效模拟器的index值（支持任意模拟器名称）
    /// 优先级：extras下第一个含index且为整数的模拟器节点
    /// 未找到或解析失败时返回 -1
    /// </summary>
    /// <param name="configJson">配置JSON字符串（格式：{"extras":{"模拟器名称":{"enable":true,"index":n,...}}}）</param>
    /// <returns>成功返回第一个有效index值，失败返回 -1</returns>
    public static int GetFirstEmulatorIndex(string configJson)
    {
        // C# 14 空值检查（包含空白字符串判断）
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return -1;
        }

        try
        {
            // 解析JSON根对象
            var rootObj = JObject.Parse(configJson);

            // 1. 安全获取 extras 节点（核心父节点）
            var extrasNode = rootObj["extras"] as JObject;
            if (extrasNode is null)
            {
                return -1;
            }

            // 2. 遍历 extras 下所有子节点（每个子节点对应一个模拟器）
            foreach (var emulatorNode in extrasNode.Properties())
            {
                // 模拟器节点名称（如 ld、mumu、nox 等）
                string emulatorName = emulatorNode.Name;

                if (emulatorNode.Value is not JObject emulatorObj)
                    continue;


                // 3. 检查当前模拟器节点是否包含有效 index
                var indexNode = emulatorObj["index"];
                if (indexNode is JValue { Type: JTokenType.Integer } indexValue)
                {
                    var index = System.Convert.ToInt32(indexValue.Value);
                    return index;
                }
            }

            return -1;
        }
        catch (JsonReaderException ex)
        {
            LoggerHelper.Error(ex);
            return -1;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error(ex);
            return -1;
        }
    }
}
