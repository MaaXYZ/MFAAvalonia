using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MFAAvalonia.Helper.Converters;

public class TitleConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // 安全解包参数（处理 UnsetValue 和 null）
        var customTitle = SafeGetValue<string>(values, 0);
        var isCustomVisible = SafeGetValue<bool>(values, 1);
        var appName = SafeGetValue<object>(values, 2);
        var resourceName = SafeGetValue<string>(values, 3);
        var isResourceVisible = SafeGetValue<bool>(values, 4);

        var result = appName?.ToString() ?? string.Empty;
        // 主逻辑
        if (isCustomVisible && !string.IsNullOrEmpty(customTitle))
            result = customTitle;

        if (isResourceVisible
            && !string.IsNullOrEmpty(resourceName)
            && !string.Equals(appName?.ToString(), resourceName, StringComparison.OrdinalIgnoreCase))
            result = $"{appName} | {resourceName}";
        return result;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// 安全获取绑定值（处理 UnsetValue 和类型转换）
    /// </summary>
    private T SafeGetValue<T>(IList<object?> values, int index, T defaultValue = default)
    {
        if (index >= values.Count) return defaultValue;

        var value = values[index];

        // 处理 Avalonia 的 UnsetValue
        if (value is UnsetValueType || value == null) return defaultValue;

        try
        {
            return (T)System.Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}
