using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace MFAAvalonia.Helper;

public static class InstanceConfigShareService
{
    public const string ProtocolSegment = "mfa-instance-sharing";
    public const string CurrentVersion = "v1";

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore,
        Converters =
        {
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false)
        }
    };

    public static string BuildExportText(string projectName, string instanceName, InstanceConfigSharePayload payload)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name is required.", nameof(projectName));

        var json = JsonConvert.SerializeObject(payload, Formatting.None, SerializerSettings);
        var compressed = Compress(Encoding.UTF8.GetBytes(json));
        var dataLine = $"{projectName}://{ProtocolSegment}/{CurrentVersion}/{Uri.EscapeDataString(instanceName)}/{ToBase64Url(compressed)}";
        return $"[MFA] {projectName} - {instanceName}\n{dataLine}";
    }

    public static InstanceConfigImportResult ParseImportText(string projectName, string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            throw new InstanceConfigImportException(InstanceConfigImportError.InvalidFormat);

        var dataLine = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains($"://{ProtocolSegment}/", StringComparison.Ordinal))
            ?? rawText.Trim();

        var marker = $"://{ProtocolSegment}/";
        var markerIndex = dataLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0)
            throw new InstanceConfigImportException(InstanceConfigImportError.InvalidFormat);

        var importedProject = dataLine[..markerIndex];
        if (!string.Equals(importedProject, projectName, StringComparison.Ordinal))
            throw new InstanceConfigImportException(InstanceConfigImportError.ProjectMismatch);

        var parts = dataLine[(markerIndex + marker.Length)..].Split('/');
        if (parts.Length < 3)
            throw new InstanceConfigImportException(InstanceConfigImportError.InvalidFormat);
        if (!string.Equals(parts[0], CurrentVersion, StringComparison.Ordinal))
            throw new InstanceConfigImportException(InstanceConfigImportError.UnsupportedVersion);

        try
        {
            var instanceName = Uri.UnescapeDataString(parts[1]);
            var compressed = FromBase64Url(string.Join('/', parts.Skip(2)));
            var json = Encoding.UTF8.GetString(Decompress(compressed));
            var payload = JsonConvert.DeserializeObject<InstanceConfigSharePayload>(json, SerializerSettings);
            if (payload?.Tasks == null)
                throw new JsonSerializationException("Missing task list.");

            return new InstanceConfigImportResult(instanceName, payload);
        }
        catch (InstanceConfigImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstanceConfigImportException(InstanceConfigImportError.InvalidFormat, ex);
        }
    }

    public static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "config" : result;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var stream = new DeflateStream(output, CompressionLevel.SmallestSize, true))
            stream.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var stream = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }
}

public sealed class InstanceConfigSharePayload
{
    [JsonProperty("ct")] public string? ControllerType { get; set; }
    [JsonProperty("cn")] public string? ControllerName { get; set; }
    [JsonProperty("rn")] public string? ResourceName { get; set; }
    [JsonProperty("t")] public List<MaaInterface.MaaInterfaceTask> Tasks { get; set; } = [];
    [JsonProperty("go")] public List<MaaInterface.MaaInterfaceSelectOption>? GlobalOptions { get; set; }
    [JsonProperty("co")] public Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>? ControllerOptions { get; set; }
    [JsonProperty("ro")] public Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>? ResourceOptions { get; set; }
}

public sealed record InstanceConfigImportResult(string InstanceName, InstanceConfigSharePayload Payload);

public enum InstanceConfigImportError
{
    InvalidFormat,
    ProjectMismatch,
    UnsupportedVersion
}

public sealed class InstanceConfigImportException : Exception
{
    public InstanceConfigImportException(InstanceConfigImportError error, Exception? innerException = null)
        : base(error.ToString(), innerException) => Error = error;

    public InstanceConfigImportError Error { get; }
}
