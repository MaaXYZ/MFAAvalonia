using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MFAAvalonia.Features.CombatStrategies;

/// <summary>客户端与后续战斗执行端共用的版本化策略格式。</summary>
public static class StrategyJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public static string Serialize(StrategyDocument document)
    {
        StrategyValidator.EnsureValid(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static StrategyDocument Deserialize(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<StrategyDocument>(json, Options)
                ?? throw new StrategyValidationException(["策略文件内容为空。"]);
            StrategyValidator.EnsureValid(result);
            return result;
        }
        catch (JsonException ex)
        {
            throw new StrategyValidationException([$"策略 JSON 格式错误：{ex.Message}"]);
        }
    }

    /// <summary>复制编辑草稿，允许保留尚未填写完整的条件。</summary>
    public static StrategyDocument Clone(StrategyDocument document) =>
        JsonSerializer.Deserialize<StrategyDocument>(JsonSerializer.Serialize(document, Options), Options)!;
}
