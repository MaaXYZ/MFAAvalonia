using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>管理内置卡死恢复选项及其目标应用配置。</summary>
public static class TaskRecoverySettings
{
    public const string OptionName = "卡死重启";
    public const string PackageOptionName = "目标应用包名";

    private static readonly string[] PackageOptionAliases = [PackageOptionName, "目标应用", "其它客户端包名"];

    public static void EnsureBuiltInOption(MaaInterface? maaInterface)
    {
        if (maaInterface == null)
            return;

        maaInterface.GlobalOption ??= [];
        if (!maaInterface.GlobalOption.Contains(OptionName, StringComparer.Ordinal))
            maaInterface.GlobalOption.Add(OptionName);

        maaInterface.Option ??= new Dictionary<string, MaaInterface.MaaInterfaceOption>(StringComparer.Ordinal);
        if (!maaInterface.Option.TryGetValue(OptionName, out var recoveryOption))
        {
            recoveryOption = new MaaInterface.MaaInterfaceOption
            {
                Name = OptionName,
                Type = "switch",
                Label = OptionName,
                Description = "检测 Android 模拟器任务长时间无回调或无成功点击，并尝试重启游戏。",
                DefaultCases = ["No"],
                Cases =
                [
                    new MaaInterface.MaaInterfaceOptionCase
                    {
                        Name = "Yes",
                        Label = "开启",
                        Option = [PackageOptionName]
                    },
                    new MaaInterface.MaaInterfaceOptionCase
                    {
                        Name = "No",
                        Label = "关闭"
                    }
                ]
            };
            maaInterface.Option[OptionName] = recoveryOption;
        }

        recoveryOption.Name ??= OptionName;
        EnsurePackageInput(maaInterface, recoveryOption);
    }

    public static bool IsEnabled(MaaInterface? maaInterface)
    {
        var selection = maaInterface?.GlobalSelectOptions?.FirstOrDefault(option => option.Name == OptionName);
        if (selection?.Index is not int index)
            return false;

        if (maaInterface?.Option?.TryGetValue(OptionName, out var definition) == true
            && definition.Cases is { } cases
            && index >= 0 && index < cases.Count)
        {
            var selectedCase = cases[index];
            if (IsEnabledCase(selectedCase.Name, selectedCase.Label))
                return true;
            if (IsDisabledCase(selectedCase.Name, selectedCase.Label))
                return false;
        }

        return index == 0;
    }

    public static string? GetTargetPackageName(MaaInterface? maaInterface)
    {
        var recoveryOption = maaInterface?.GlobalSelectOptions?.FirstOrDefault(option => option.Name == OptionName);
        foreach (var name in PackageOptionAliases)
        {
            var value = recoveryOption?.SubOptions?.FirstOrDefault(option => option.Name == name)
                ?.Data?.GetValueOrDefault("package_name");
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static void EnsurePackageInput(MaaInterface maaInterface, MaaInterface.MaaInterfaceOption recoveryOption)
    {
        maaInterface.Option ??= new Dictionary<string, MaaInterface.MaaInterfaceOption>(StringComparer.Ordinal);
        var targetCase = recoveryOption.Cases?.FirstOrDefault(item => IsEnabledCase(item.Name, item.Label))
            ?? recoveryOption.Cases?.FirstOrDefault();
        if (targetCase == null)
            return;

        targetCase.Option ??= [];
        var existingPackageOption = targetCase.Option.FirstOrDefault(name =>
            PackageOptionAliases.Contains(name, StringComparer.Ordinal)
            && maaInterface.Option.ContainsKey(name));
        if (existingPackageOption != null)
            return;

        if (!targetCase.Option.Contains(PackageOptionName, StringComparer.Ordinal))
            targetCase.Option.Add(PackageOptionName);

        if (!maaInterface.Option.ContainsKey(PackageOptionName))
        {
            maaInterface.Option[PackageOptionName] = new MaaInterface.MaaInterfaceOption
            {
                Name = PackageOptionName,
                Type = "input",
                Label = "目标应用包名",
                Description = "填写 Android 应用包名，用于卡死后重新启动游戏。",
                Inputs =
                [
                    new MaaInterface.MaaInterfaceOptionInput
                    {
                        Name = "package_name",
                        Label = "应用包名",
                        Default = string.Empty,
                        PipelineType = "string"
                    }
                ]
            };
        }
    }

    private static bool IsEnabledCase(string? name, string? label) =>
        string.Equals(name, "Yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Enabled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, "开启", StringComparison.Ordinal)
        || string.Equals(label, "启用", StringComparison.Ordinal);

    private static bool IsDisabledCase(string? name, string? label) =>
        string.Equals(name, "No", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Disabled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, "关闭", StringComparison.Ordinal)
        || string.Equals(label, "停用", StringComparison.Ordinal);
}
