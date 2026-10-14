using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace MFAAvalonia.Helper;

internal sealed record EvidenceFileStamp(long Length, DateTime? LastWriteUtc);

internal sealed class TaskEvidenceSnapshot
{
    internal required DateTimeOffset StartedAt { get; init; }
    internal required Dictionary<string, long> LogLengths { get; init; }
    internal required Dictionary<string, EvidenceFileStamp> ExistingErrorImages { get; init; }
    internal required IReadOnlyList<string> Warnings { get; init; }
    internal required bool Isolated { get; init; }
}

internal enum TaskEvidenceBuildStatus
{
    Success,
    ConcurrentInstance,
    NoEvidence,
    RawTooLarge,
    BundleTooLarge,
    BuildFailed
}

internal sealed record DiagnosticLogContent(
    string Source,
    string Kind,
    string Content,
    long RawBytes);

internal sealed record DiagnosticLogBuildResult(
    TaskEvidenceBuildStatus Status,
    IReadOnlyList<DiagnosticLogContent> Logs,
    long RawBytes,
    bool Truncated,
    IReadOnlyList<string> Warnings);

internal sealed record ImageEvidenceBuildResult(
    TaskEvidenceBuildStatus Status,
    byte[]? Data,
    int ImageCount,
    long RawBytes,
    long BundleBytes,
    IReadOnlyList<string> Warnings);

internal sealed record TaskEvidenceBuildResult(
    DiagnosticLogBuildResult Logs,
    ImageEvidenceBuildResult Images);

internal sealed class EvidenceFileSlice : IDisposable
{
    internal required FileStream Source { get; init; }
    internal required string Name { get; init; }
    internal required long Start { get; set; }
    internal required long End { get; init; }
    internal required DateTime LastWriteUtc { get; init; }
    internal required EvidenceFileStamp Stamp { get; init; }

    public void Dispose() => Source.Dispose();
}

internal sealed class TaskEvidenceSelection : IDisposable
{
    internal required bool Isolated { get; init; }
    internal required DateTimeOffset StartedAt { get; init; }
    internal required Dictionary<string, EvidenceFileStamp> ExistingErrorImages { get; init; }
    internal required List<EvidenceFileSlice> Logs { get; init; }
    internal required List<EvidenceFileSlice> Images { get; init; }
    internal required List<string> Warnings { get; init; }

    public void Dispose()
    {
        foreach (var item in Logs) item.Dispose();
        foreach (var item in Images) item.Dispose();
        Logs.Clear();
        Images.Clear();
    }
}

internal sealed class TaskImageRefresh : IDisposable
{
    internal required List<EvidenceFileSlice> Images { get; init; }
    internal required List<string> Warnings { get; init; }

    public void Dispose()
    {
        foreach (var item in Images) item.Dispose();
        Images.Clear();
    }
}

internal static class TaskDiagnostics
{
    private const long MaxImageRawBytes = 5 * 1024 * 1024;
    private const int MaxImageBundleBytes = 5 * 1024 * 1024;
    private const int MaxImageFiles = 128;
    private const int MaxLogRawBytes = 1024 * 1024;
    private const int MaxLogFileBytes = 512 * 1024;
    private const int MaxLogFiles = 128;
    private const int LogPreludeBytes = 64 * 1024;
    private const int ExceptionLogTailBytes = 256 * 1024;
    private const int MaxExceptionLogFiles = 5;
    private const int MaxDiscoveryEntries = 8 * 1024;
    private const int MaxDiscoveryDepth = 16;
    private const int MaxWarnings = 32;
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    internal static TaskEvidenceSnapshot CaptureStart(bool isolated, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var logs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, EvidenceFileStamp>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        if (!isolated)
        {
            return new TaskEvidenceSnapshot
            {
                StartedAt = startedAt,
                LogLengths = logs,
                ExistingErrorImages = images,
                Warnings = warnings,
                Isolated = false
            };
        }

        foreach (var path in EnumerateEvidenceFiles(warnings, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(path);
            try
            {
                var info = new FileInfo(path);
                if (IsLog(relative))
                {
                    logs[relative] = info.Length;
                }
                else if (IsErrorImage(relative))
                {
                    var stamp = new EvidenceFileStamp(info.Length, TryGetLastWriteUtc(info));
                    if (!stamp.LastWriteUtc.HasValue || stamp.LastWriteUtc.Value < startedAt.UtcDateTime)
                        images[relative] = stamp;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, $"snapshot_metadata_failed:{Path.GetFileName(path)}:{ex.GetType().Name}");
            }
        }

        return new TaskEvidenceSnapshot
        {
            StartedAt = startedAt,
            LogLengths = logs,
            ExistingErrorImages = images,
            Warnings = warnings,
            Isolated = true
        };
    }

    internal static TaskEvidenceSelection CaptureEnd(
        TaskEvidenceSnapshot snapshot,
        bool isolatedAtEnd,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>(snapshot.Warnings);
        var selection = new TaskEvidenceSelection
        {
            Isolated = snapshot.Isolated && isolatedAtEnd,
            StartedAt = snapshot.StartedAt,
            ExistingErrorImages = snapshot.ExistingErrorImages,
            Logs = [],
            Images = [],
            Warnings = warnings
        };
        if (!selection.Isolated) return selection;

        try
        {
            foreach (var path in EnumerateEvidenceFiles(warnings, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Relative(path);
                if (IsErrorImage(relative))
                {
                    if (selection.Images.Count >= MaxImageFiles)
                    {
                        AddWarning(warnings, $"image_file_limit_reached:{MaxImageFiles}");
                        continue;
                    }
                    var image = TryOpenImage(path, relative, snapshot, warnings);
                    if (image != null) selection.Images.Add(image);
                }
                else if (IsLog(relative))
                {
                    if (selection.Logs.Count >= MaxLogFiles)
                    {
                        AddWarning(warnings, $"log_file_limit_reached:{MaxLogFiles}");
                        continue;
                    }
                    var log = TryOpenLog(path, relative, snapshot, warnings);
                    if (log != null) selection.Logs.Add(log);
                }
            }
            SortSlices(selection.Logs);
            SortSlices(selection.Images);
            return selection;
        }
        catch
        {
            selection.Dispose();
            throw;
        }
    }

    internal static TaskImageRefresh CaptureImageRefresh(
        TaskEvidenceSelection selection,
        CancellationToken cancellationToken = default)
    {
        var refresh = new TaskImageRefresh { Images = [], Warnings = [] };
        if (!selection.Isolated) return refresh;

        try
        {
            foreach (var path in EnumerateEvidenceFiles(refresh.Warnings, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Relative(path);
                if (!IsErrorImage(relative)) continue;
                if (refresh.Images.Count >= MaxImageFiles)
                {
                    AddWarning(refresh.Warnings, $"image_file_limit_reached:{MaxImageFiles}");
                    break;
                }
                var image = TryOpenImage(path, relative, selection.StartedAt,
                    selection.ExistingErrorImages, refresh.Warnings);
                if (image != null) refresh.Images.Add(image);
            }
            SortSlices(refresh.Images);
            return refresh;
        }
        catch
        {
            refresh.Dispose();
            throw;
        }
    }

    internal static void ApplyImageRefresh(TaskEvidenceSelection selection, TaskImageRefresh refresh)
    {
        foreach (var image in refresh.Images.ToArray())
        {
            var index = selection.Images.FindIndex(existing =>
                string.Equals(existing.Name, image.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                selection.Images.Add(image);
                if (selection.Images.Count > MaxImageFiles)
                {
                    var oldest = selection.Images
                        .OrderBy(item => item.LastWriteUtc)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .First();
                    selection.Images.Remove(oldest);
                    oldest.Dispose();
                    AddWarning(selection.Warnings, $"image_file_limit_reached:{MaxImageFiles}");
                }
            }
            else if (selection.Images[index].Stamp != image.Stamp)
            {
                selection.Images[index].Dispose();
                selection.Images[index] = image;
            }
            else
            {
                image.Dispose();
            }
        }
        refresh.Images.Clear();
        foreach (var warning in refresh.Warnings) AddWarning(selection.Warnings, warning);
        SortSlices(selection.Images);
    }

    internal static TaskEvidenceBuildResult Build(
        TaskEvidenceSelection selection,
        bool buildImages,
        CancellationToken cancellationToken = default)
    {
        if (!selection.Isolated)
        {
            return new TaskEvidenceBuildResult(
                EmptyLogs(TaskEvidenceBuildStatus.ConcurrentInstance, selection.Warnings),
                EmptyImages(TaskEvidenceBuildStatus.ConcurrentInstance, selection.Warnings));
        }
        var logs = BuildLogs(selection.Logs, selection.Warnings, cancellationToken);
        if (!buildImages)
            return new TaskEvidenceBuildResult(
                logs, EmptyImages(TaskEvidenceBuildStatus.NoEvidence, selection.Warnings));

        try
        {
            return new TaskEvidenceBuildResult(
                logs, BuildImages(selection.Images, selection.Warnings, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            var warnings = new List<string>(selection.Warnings);
            AddWarning(warnings, $"build_images_failed:{ex.GetType().Name}");
            return new TaskEvidenceBuildResult(
                logs, EmptyImages(TaskEvidenceBuildStatus.BuildFailed, warnings));
        }
    }

    internal static DiagnosticLogBuildResult BuildRecentLogs(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var selected = new List<EvidenceFileSlice>();
        try
        {
            foreach (var path in EnumerateEvidenceFiles(warnings, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Relative(path);
                if (!IsLog(relative)) continue;
                if (selected.Count >= MaxLogFiles)
                {
                    AddWarning(warnings, $"log_file_limit_reached:{MaxLogFiles}");
                    break;
                }
                var slice = TryOpenWholeTail(path, relative, ExceptionLogTailBytes, warnings);
                if (slice != null) selected.Add(slice);
            }
            var retained = selected
                .OrderBy(item => GetLogPriority(item.Name))
                .ThenByDescending(item => item.LastWriteUtc)
                .Take(MaxExceptionLogFiles)
                .ToHashSet();
            return BuildLogs(retained, warnings, cancellationToken);
        }
        finally
        {
            foreach (var item in selected) item.Dispose();
        }
    }

    private static DiagnosticLogBuildResult BuildLogs(
        IEnumerable<EvidenceFileSlice> candidates,
        IReadOnlyList<string> inheritedWarnings,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>(inheritedWarnings);
        var logs = new List<DiagnosticLogContent>();
        var rawBytes = 0L;
        var truncated = false;
        foreach (var item in candidates
                     .OrderBy(candidate => GetLogPriority(candidate.Name))
                     .ThenByDescending(candidate => candidate.LastWriteUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBudget = MaxLogRawBytes - rawBytes;
            if (remainingBudget <= 0)
            {
                truncated = true;
                break;
            }
            var selectedLength = item.End - item.Start;
            var readLength = Math.Min(selectedLength, Math.Min(MaxLogFileBytes, remainingBudget));
            if (readLength <= 0) continue;
            var readStart = item.End - readLength;
            try
            {
                var data = ReadBytes(item, readStart, readLength, cancellationToken);
                if (data.Length == 0) continue;
                var content = Encoding.UTF8.GetString(data).Trim('\0');
                if (string.IsNullOrWhiteSpace(content)) continue;
                logs.Add(new DiagnosticLogContent(item.Name, GetLogKind(item.Name), content, data.Length));
                rawBytes += data.Length;
                truncated |= readLength < selectedLength;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, $"read_log_failed:{item.Name}:{ex.GetType().Name}");
            }
        }

        return logs.Count == 0
            ? EmptyLogs(TaskEvidenceBuildStatus.NoEvidence, warnings)
            : new DiagnosticLogBuildResult(TaskEvidenceBuildStatus.Success, logs, rawBytes, truncated, warnings);
    }

    private static ImageEvidenceBuildResult BuildImages(
        IReadOnlyCollection<EvidenceFileSlice> selected,
        IReadOnlyList<string> inheritedWarnings,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>(inheritedWarnings);
        if (selected.Count == 0) return EmptyImages(TaskEvidenceBuildStatus.NoEvidence, warnings);

        var rawBytes = 0L;
        foreach (var item in selected)
        {
            var length = item.End - item.Start;
            if (length < 0 || length > MaxImageRawBytes - rawBytes)
            {
                var observedRawBytes = length < 0 || length > long.MaxValue - rawBytes
                    ? long.MaxValue
                    : rawBytes + length;
                return new ImageEvidenceBuildResult(
                    TaskEvidenceBuildStatus.RawTooLarge, null, selected.Count,
                    observedRawBytes, 0, warnings);
            }
            rawBytes += length;
        }

        using var output = new MemoryStream((int)Math.Min(MaxImageBundleBytes + 1L, rawBytes + 64 * 1024L));
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in selected.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
                using var target = entry.Open();
                CopySlice(item, target, cancellationToken);
            }
        }

        if (output.Length > MaxImageBundleBytes)
            return new ImageEvidenceBuildResult(
                TaskEvidenceBuildStatus.BundleTooLarge, null, selected.Count,
                rawBytes, output.Length, warnings);
        return new ImageEvidenceBuildResult(
            TaskEvidenceBuildStatus.Success, output.ToArray(), selected.Count,
            rawBytes, output.Length, warnings);
    }

    private static EvidenceFileSlice? TryOpenLog(
        string path,
        string relative,
        TaskEvidenceSnapshot snapshot,
        List<string> warnings)
    {
        var slice = TryOpenWholeTail(path, relative, long.MaxValue, warnings);
        if (slice == null) return null;
        var length = slice.End;
        var modified = ModifiedSince(slice.LastWriteUtc, snapshot.StartedAt.UtcDateTime);
        if (snapshot.LogLengths.TryGetValue(relative, out var previousLength))
        {
            if (length > previousLength)
            {
                slice.Start = Math.Max(0, previousLength - LogPreludeBytes);
            }
            else if (length < previousLength && modified && length > 0)
            {
                AddWarning(warnings, $"rotated_log_included_whole:{relative}");
            }
            else if (length == previousLength && modified && length > 0)
            {
                AddWarning(warnings, $"log_changed_during_start_snapshot:{relative}");
                slice.Start = Math.Max(0, length - LogPreludeBytes);
            }
            else
            {
                slice.Dispose();
                return null;
            }
        }
        else if (!modified || length == 0)
        {
            slice.Dispose();
            return null;
        }
        else
        {
            AddWarning(warnings, $"new_log_included_whole:{relative}");
        }
        return slice;
    }

    private static EvidenceFileSlice? TryOpenImage(
        string path,
        string relative,
        TaskEvidenceSnapshot snapshot,
        List<string> warnings) =>
        TryOpenImage(path, relative, snapshot.StartedAt, snapshot.ExistingErrorImages, warnings);

    private static EvidenceFileSlice? TryOpenImage(
        string path,
        string relative,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, EvidenceFileStamp> existingImages,
        List<string> warnings)
    {
        var slice = TryOpenWholeTail(path, relative, long.MaxValue, warnings);
        if (slice == null) return null;
        if (!ModifiedSince(slice.LastWriteUtc, startedAt.UtcDateTime)
            || existingImages.TryGetValue(relative, out var previous) && previous == slice.Stamp)
        {
            slice.Dispose();
            return null;
        }
        return slice;
    }

    private static EvidenceFileSlice? TryOpenWholeTail(
        string path,
        string relative,
        long maxLength,
        List<string> warnings)
    {
        FileStream? source = null;
        try
        {
            source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var length = source.Length;
            var lastWriteUtc = File.GetLastWriteTimeUtc(source.SafeFileHandle);
            return new EvidenceFileSlice
            {
                Source = source,
                Name = relative,
                Start = Math.Max(0, length - maxLength),
                End = length,
                LastWriteUtc = lastWriteUtc,
                Stamp = new EvidenceFileStamp(length, lastWriteUtc)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            source?.Dispose();
            AddWarning(warnings, $"open_evidence_failed:{relative}:{ex.GetType().Name}");
            return null;
        }
    }

    private static byte[] ReadBytes(
        EvidenceFileSlice item,
        long start,
        long length,
        CancellationToken cancellationToken)
    {
        item.Source.Position = start;
        using var output = new MemoryStream((int)Math.Min(length, int.MaxValue));
        var remaining = length;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = item.Source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        return output.ToArray();
    }

    private static void CopySlice(
        EvidenceFileSlice item,
        Stream target,
        CancellationToken cancellationToken)
    {
        item.Source.Position = item.Start;
        var remaining = item.End - item.Start;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = item.Source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException($"Evidence file became shorter: {item.Name}");
            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static IEnumerable<string> EnumerateEvidenceFiles(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Root, string Directory, int Depth)>();
        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        foreach (var root in new[] { AppPaths.LogsDirectory, Path.Combine(AppPaths.DataRoot, "debug") })
        {
            var fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot) || !IsInsideRoot(dataRoot, fullRoot)) continue;
            try
            {
                if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    AddWarning(warnings, "discovery_root_reparse_point_skipped");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, $"discovery_root_attributes_failed:{ex.GetType().Name}");
                continue;
            }
            pending.Push((fullRoot, fullRoot, 0));
        }

        var visitedEntries = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (root, directory, depth) = pending.Pop();
            IEnumerator<string> enumerator;
            try
            {
                enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, $"discovery_open_directory_failed:{ex.GetType().Name}");
                continue;
            }
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool hasNext;
                    try { hasNext = enumerator.MoveNext(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AddWarning(warnings, $"discovery_read_directory_failed:{ex.GetType().Name}");
                        break;
                    }
                    if (!hasNext) break;
                    if (++visitedEntries > MaxDiscoveryEntries)
                    {
                        AddWarning(warnings, $"discovery_entry_limit_reached:{MaxDiscoveryEntries}");
                        yield break;
                    }

                    var path = enumerator.Current;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AddWarning(warnings, $"discovery_attributes_failed:{ex.GetType().Name}");
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        AddWarning(warnings, "discovery_reparse_point_skipped");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (string.Equals(Path.GetFileName(path), "vision", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (depth >= MaxDiscoveryDepth)
                        {
                            AddWarning(warnings, $"discovery_depth_limit_reached:{MaxDiscoveryDepth}");
                            continue;
                        }
                        var fullDirectory = Path.GetFullPath(path);
                        if (IsInsideRoot(root, fullDirectory))
                            pending.Push((root, fullDirectory, depth + 1));
                        continue;
                    }

                    var fullPath = Path.GetFullPath(path);
                    if (!IsInsideRoot(root, fullPath) || !seen.Add(fullPath)) continue;
                    if (!IsInsideRoot(dataRoot, fullPath))
                    {
                        AddWarning(warnings, "discovery_outside_data_root_skipped");
                        continue;
                    }
                    var relative = Relative(fullPath);
                    if (IsLog(relative) || IsErrorImage(relative)) yield return fullPath;
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }
    }

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool ModifiedSince(DateTime modifiedUtc, DateTime startedUtc) =>
        modifiedUtc + MtimeTolerance >= startedUtc;

    private static DateTime? TryGetLastWriteUtc(FileInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void SortSlices(List<EvidenceFileSlice> slices) =>
        slices.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

    private static int GetLogPriority(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("maafw.log", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.StartsWith("maa.log", StringComparison.OrdinalIgnoreCase)) return 1;
        if (fileName.StartsWith("log-", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static string GetLogKind(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("maafw.log", StringComparison.OrdinalIgnoreCase)) return "maafw";
        if (fileName.StartsWith("maa.log", StringComparison.OrdinalIgnoreCase)) return "maa";
        if (fileName.StartsWith("log-", StringComparison.OrdinalIgnoreCase)) return "mfa";
        return "other";
    }

    private static DiagnosticLogBuildResult EmptyLogs(
        TaskEvidenceBuildStatus status,
        IReadOnlyList<string> warnings) =>
        new(status, Array.Empty<DiagnosticLogContent>(), 0, false, warnings.ToArray());

    private static ImageEvidenceBuildResult EmptyImages(
        TaskEvidenceBuildStatus status,
        IReadOnlyList<string> warnings) =>
        new(status, null, 0, 0, 0, warnings.ToArray());

    internal static void AddWarning(List<string> warnings, string warning)
    {
        var bounded = warning.Length <= 200 ? warning : warning[..200];
        if (warnings.Count < MaxWarnings && !warnings.Contains(bounded, StringComparer.Ordinal))
            warnings.Add(bounded);
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(AppPaths.DataRoot, path).Replace('\\', '/');

    private static bool IsLog(string path) =>
        path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        || path.Contains(".log.", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorImage(string path) =>
        path.Contains("/on_error/", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
}
