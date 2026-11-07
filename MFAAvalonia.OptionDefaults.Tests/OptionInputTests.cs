using Avalonia.Platform.Storage;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public class OptionInputTests
{
    [Fact]
    public void ProtocolFieldsDeserializeIndependently()
    {
        var input = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOptionInput>(
            """{"name":"config","input_type":"file","pipeline_type":"int","type":"directory"}""")!;
        Assert.Equal("file", input.EditorType);
        Assert.Equal("int", input.PipelineType);
        Assert.False(input.UseBooleanSwitch);
        var legacy = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOptionInput>(
            """{"name":"enabled","pipeline_type":"bool"}""")!;
        Assert.True(legacy.UseBooleanSwitch);
        Assert.True(JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOptionInput>(
            """{"type":"password"}""")!.IsPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("secret-token-123")]
    [InlineData("中文密钥\"\\")]
    public void PasswordRoundTripsEncryptedWithoutInterfaceOrChangingRuntimeValues(string secret)
    {
        var input = new MaaInterface.MaaInterfaceOptionInput { Name = "credential", Password = true, Default = "ignored" };
        var definition = new MaaInterface.MaaInterfaceOption { Type = "input", Inputs = [input] };
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "login", Data = new() { ["credential"] = secret, ["user"] = "alice" },
            SubOptions = [new() { Name = "nested", Data = new() { ["credential"] = secret } }]
        };
        option.InitializeInputData(definition);
        option.SubOptions[0].InitializeInputData(definition);
        var json = JsonConvert.SerializeObject(option);
        var token = JObject.Parse(json);
        Assert.Equal(JTokenType.Object, token["data"]!["credential"]!.Type);
        Assert.Equal("alice", token["data"]!["user"]!.Value<string>());
        Assert.Equal(secret, option.Data["credential"]);
        var loaded = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(json)!;
        Assert.Equal(secret, loaded.Data!["credential"]);
        Assert.Equal(secret, loaded.SubOptions![0].Data!["credential"]);
        Assert.Equal(JTokenType.Object, JObject.Parse(JsonConvert.SerializeObject(loaded))["data"]!["credential"]!.Type);
        Assert.Null(input.Default);
        Assert.False(OptionDefaultReset.HasInputDefault(input));
    }

    [Fact]
    public void LegacyPasswordsInInactiveBranchesAreEncryptedWithoutChangingValues()
    {
        var parent = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "parent", Index = 0,
            SubOptions = [new() { Name = "login", Data = new() { ["key"] = "legacy-secret" } }]
        };
        var definition = new MaaInterface
        {
            Option = new()
            {
                ["parent"] = new() { Type = "select", Cases = [new() { Name = "off" }, new() { Name = "on", Option = ["login"] }] },
                ["login"] = new() { Type = "input", Inputs = [new() { Name = "key", Password = true }] }
            }
        };
        TaskLoader.SetDefaultOptionValue(definition, parent);
        Assert.Equal(0, parent.Index);
        Assert.Equal("legacy-secret", parent.SubOptions![0].Data!["key"]);
        Assert.DoesNotContain("legacy-secret", JsonConvert.SerializeObject(parent));
    }

    [Fact]
    public void ClonedInactiveSubOptionsKeepPasswordProtection()
    {
        var child = new MaaInterface.MaaInterfaceSelectOption { Name = "login", Data = new() { ["key"] = "secret" } };
        child.InitializeInputData(new() { Type = "input", Inputs = [new() { Name = "key", Password = true }] });
        var parent = new MaaInterface.MaaInterfaceSelectOption { Name = "inactive", SubOptions = [child] };
        var clone = (List<MaaInterface.MaaInterfaceSelectOption>)typeof(TaskLoader)
            .GetMethod("CloneSubOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new List<MaaInterface.MaaInterfaceSelectOption> { parent }])!;
        Assert.NotSame(child.Data, clone[0].SubOptions![0].Data);
        Assert.Equal("secret", clone[0].SubOptions![0].Data!["key"]);
        Assert.DoesNotContain("secret", JsonConvert.SerializeObject(clone));
        Assert.Equal("secret", JsonConvert.DeserializeObject<List<MaaInterface.MaaInterfaceSelectOption>>(
            JsonConvert.SerializeObject(clone))![0].SubOptions![0].Data!["key"]);
    }

    [Fact]
    public void LegacyPlainDataStillLoadsAndPasswordDefaultsAreIgnoredRegardlessOfPropertyOrder()
    {
        var option = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(
            """{"name":"login","data":{"user":"alice","credential":"legacy"}}""")!;
        Assert.Equal("alice", option.Data!["user"]);
        Assert.Equal("legacy", option.Data["credential"]);
        foreach (var json in new[]
        {
            """{"name":"credential","default":"unsafe","password":true}""",
            """{"name":"credential","password":true,"default":"unsafe"}"""
        })
        {
            var input = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOptionInput>(json)!;
            var fresh = new MaaInterface.MaaInterfaceSelectOption();
            fresh.InitializeInputData(new() { Type = "input", Inputs = [input] });
            Assert.Equal(string.Empty, fresh.Data!["credential"]);
        }
    }

    [Theory]
    [InlineData("file", false, false)]
    [InlineData("directory", false, false)]
    [InlineData("file", true, true)]
    [InlineData("directory", true, true)]
    [InlineData("file", true, false)]
    [InlineData("directory", true, false)]
    public async Task PickerReturnsNativePathOrNullOnCancelAndUnsupportedPlatforms(string editorType, bool supported, bool cancel)
    {
        const string path = @"C:\测试文件夹\config.json";
        var provider = DispatchProxy.Create<IStorageProvider, StorageProxy>();
        var proxy = (StorageProxy)(object)provider;
        proxy.Handler = (method, args) => method switch
        {
            "get_CanOpen" or "get_CanPickFolder" => supported,
            "OpenFilePickerAsync" => Files((FilePickerOpenOptions)args![0]!),
            "OpenFolderPickerAsync" => Task.FromResult<IReadOnlyList<IStorageFolder>>(cancel ? [] : [Item<IStorageFolder>(path)]),
            _ => throw new NotSupportedException(method)
        };
        Task<IReadOnlyList<IStorageFile>> Files(FilePickerOpenOptions options)
        {
            Assert.False(options.AllowMultiple);
            Assert.Contains(FilePickerFileTypes.All, options.FileTypeFilter!);
            return Task.FromResult<IReadOnlyList<IStorageFile>>(cancel ? [] : [Item<IStorageFile>(path)]);
        }
        var picker = typeof(TaskOptionGenerator).Assembly.GetType("MFAAvalonia.Helper.OptionInputPicker")!
            .GetMethod("PickPathAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = await (Task<string?>)picker.Invoke(null, [provider, editorType])!;
        Assert.Equal(supported && !cancel ? path : null, result);
    }

    private static T Item<T>(string path) where T : class
    {
        var item = DispatchProxy.Create<T, StorageProxy>();
        ((StorageProxy)(object)item).Handler = (method, _) => method == "get_Path"
            ? new Uri(path) : throw new NotSupportedException(method);
        return item;
    }

    public class StorageProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
    }
}
