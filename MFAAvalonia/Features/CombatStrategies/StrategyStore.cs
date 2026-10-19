using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MFAAvalonia.Features.CombatStrategies;

/// <summary>使用独立文件保存策略，失败时显式报告并保留原文件。</summary>
public sealed class StrategyStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public StrategyCatalog List()
    {
        var items = new List<StrategySummary> { new("default", "默认策略", true) };
        var errors = new List<string>();
        if (!Directory.Exists(DirectoryPath)) return new(items, errors);
        try
        {
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var id = Path.GetFileNameWithoutExtension(path);
                    if (id == "default") throw new ArgumentException("用户文件不能使用内置默认策略的保留标识。");
                    var document = Load(id);
                    items.Add(new(document.Id, document.Name));
                }
                catch (Exception ex) when (IsFileError(ex))
                { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
            }
        }
        catch (Exception ex) when (IsFileError(ex)) { errors.Add($"无法读取策略目录：{ex.Message}"); }
        return new(items.Take(1).Concat(items.Skip(1).OrderBy(x => x.Name, StringComparer.CurrentCulture)).ToList(), errors);
    }

    public StrategyDocument Load(string id)
    {
        if (id == "default") return StrategyDefaults.Create();
        var document = StrategyJson.Deserialize(File.ReadAllText(GetPath(id), Encoding.UTF8));
        if (document.Id != id) throw new StrategyValidationException(["策略标识与文件名不一致。"]);
        return document;
    }

    public void Save(StrategyDocument document)
    {
        if (document.Id == "default") throw new InvalidOperationException("内置默认策略不能覆盖，请先复制。");
        var path = GetPath(document.Id);
        var json = StrategyJson.Serialize(document);
        Directory.CreateDirectory(DirectoryPath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // 清理临时文件不能掩盖真正的保存错误。
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Delete(string id)
    {
        if (id == "default") throw new InvalidOperationException("内置默认策略不能删除。");
        File.Delete(GetPath(id));
    }

    private string GetPath(string id)
    {
        if (!StrategyValidator.IsId(id)) throw new ArgumentException("策略标识无效。", nameof(id));
        return Path.Combine(DirectoryPath, id + ".json");
    }

    public static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or StrategyValidationException;
}
