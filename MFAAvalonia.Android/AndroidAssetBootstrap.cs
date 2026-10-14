using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Android.Content;

namespace MFAAvalonia.Android;

internal static class AndroidAssetBootstrap
{
    private const string AssetRoot = "MfaPackage";
    private const string PackageAssetName = "package.zip";
    private const string PackageFingerprintAssetName = "package.sha256";
    private const string PackageMarker = ".mfa-package.sha256";
    private const string PackageEntriesMarker = ".mfa-package.entries";
    private const string TransactionEntriesMarker = ".mfa-transaction.entries";
    private const string TransactionOriginalEntriesMarker = ".mfa-transaction.original.entries";
    private const string TransactionPreviousFingerprintMarker = ".mfa-transaction.previous.sha256";
    private const string TransactionPreviousEntriesMarker = ".mfa-transaction.previous.entries";
    private const string StagingDirectoryPrefix = ".mfa-package-staging-";
    private const string BackupDirectoryPrefix = ".mfa-package-backup-";
    private const string UserConfigEntry = "config";

    // Old Android builds did not record payload ownership. These are the only legacy paths that
    // may be removed during the first managed upgrade; user config, logs and cache are excluded.
    private static readonly string[] LegacyManagedEntries =
    [
        "agent", "data", "locales", "python", "resource", "Resource", "tasks",
        "interface.json", "interface.jsonc", "changes.json", "maa-project.json",
        ".python-version", "pyproject.toml", "uv.lock"
    ];

    public static void EnsureExtracted(Context context)
    {
        try
        {
            // 中文：官方空壳 APK 默认没有 MfaPackage 载荷，此时直接跳过，不创建文件也不记录异常。
            // English: The official shell APK has no MfaPackage payload by default; skip without creating files or logging an error.
            if (!HasEmbeddedPackage(context))
                return;

            var targetRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var marker = Path.Combine(targetRoot, PackageMarker);
            var packageFingerprint = ComputePackageFingerprint(context);
            RecoverInterruptedReplacement(targetRoot, packageFingerprint);
            if ((File.Exists(Path.Combine(targetRoot, "interface.json"))
                 || File.Exists(Path.Combine(targetRoot, "interface.jsonc")))
                && File.Exists(marker)
                && string.Equals(File.ReadAllText(marker).Trim(), packageFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var packageStream = OpenAsset(context, PackageAssetName);
            ReplacePackage(packageStream, targetRoot, packageFingerprint);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MFAAvalonia", $"Asset bootstrap failed: {ex}");
        }
    }

    private static bool HasEmbeddedPackage(Context context)
    {
        var assets = context.Assets ?? throw new InvalidOperationException("Android AssetManager is unavailable.");
        var assetNames = assets.List(AssetRoot) ?? [];
        var hasArchive = Array.Exists(assetNames, name => string.Equals(name, PackageAssetName, StringComparison.Ordinal));
        var hasFingerprint = Array.Exists(assetNames, name => string.Equals(name, PackageFingerprintAssetName, StringComparison.Ordinal));

        if (!hasArchive && !hasFingerprint)
            return false;

        // 中文：只出现其中一个文件说明 APK 打包不完整，不能静默使用损坏的载荷。
        // English: If only one file exists, APK packaging is incomplete and the corrupt payload must not be used silently.
        if (!hasArchive || !hasFingerprint)
            throw new InvalidDataException("The embedded Android resource package is incomplete.");

        return true;
    }

    private static string ComputePackageFingerprint(Context context)
    {
        using var stream = OpenAsset(context, PackageFingerprintAssetName);
        using var reader = new StreamReader(stream);
        var fingerprint = reader.ReadToEnd().Trim();
        if (fingerprint.Length == 0)
            throw new InvalidDataException("The Android package fingerprint is empty.");

        return fingerprint;
    }

    private static void ReplacePackage(Stream packageStream, string targetRoot, string packageFingerprint)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(targetRoot, $"{StagingDirectoryPrefix}{transactionId}");
        var backupRoot = Path.Combine(targetRoot, $"{BackupDirectoryPrefix}{transactionId}");
        var backupCanBeDeleted = false;
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var newEntries = ExtractPackage(packageStream, stagingRoot);
            ValidateStagedPackage(stagingRoot);

            // Config is seeded by the payload on first install, but becomes user-owned as soon as
            // it exists. An APK upgrade must not replace notification settings or other choices
            // with the defaults bundled in the new payload. Older builds may still list config in
            // their ownership manifest, so filter it from both the old and new managed entry sets.
            var preserveUserConfig = EntryExists(targetRoot, UserConfigEntry);
            var managedNewEntries = preserveUserConfig
                ? newEntries.Where(entry => !string.Equals(entry, UserConfigEntry, StringComparison.Ordinal)).ToArray()
                : newEntries;

            var previousEntries = ReadManagedEntries(targetRoot);
            var entriesToReplace = previousEntries
                .Concat(managedNewEntries)
                .Concat(GetLegacyManagedEntries(targetRoot))
                .Where(entry => !preserveUserConfig
                                || !string.Equals(entry, UserConfigEntry, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var originalEntries = entriesToReplace
                .Where(entry => EntryExists(targetRoot, entry))
                .ToArray();

            Directory.CreateDirectory(backupRoot);
            File.WriteAllLines(Path.Combine(backupRoot, TransactionEntriesMarker), entriesToReplace);
            File.WriteAllLines(Path.Combine(backupRoot, TransactionOriginalEntriesMarker), originalEntries);
            CopyFileIfExists(Path.Combine(targetRoot, PackageMarker), Path.Combine(backupRoot, TransactionPreviousFingerprintMarker));
            CopyFileIfExists(Path.Combine(targetRoot, PackageEntriesMarker), Path.Combine(backupRoot, TransactionPreviousEntriesMarker));
            // The fingerprint is the commit marker. Remove it before moving any managed entry so
            // recovery cannot mistake an interrupted same-fingerprint repair for a committed one.
            File.Delete(Path.Combine(targetRoot, PackageMarker));
            try
            {
                foreach (var entry in entriesToReplace)
                    MoveEntryIfExists(targetRoot, backupRoot, entry);

                foreach (var entry in managedNewEntries)
                    MoveEntryIfExists(stagingRoot, targetRoot, entry);

                WriteAllTextAtomically(
                    Path.Combine(targetRoot, PackageEntriesMarker),
                    string.Join(Environment.NewLine, managedNewEntries.Order(StringComparer.Ordinal)) + Environment.NewLine);
                WriteAllTextAtomically(Path.Combine(targetRoot, PackageMarker), packageFingerprint);
                backupCanBeDeleted = true;
            }
            catch (Exception replacementException)
            {
                try
                {
                    RestoreBackup(targetRoot, backupRoot, entriesToReplace, originalEntries);
                    backupCanBeDeleted = true;
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Android resource replacement and rollback both failed. The backup is retained for recovery on the next launch.",
                        replacementException,
                        rollbackException);
                }

                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (backupCanBeDeleted)
                TryDeleteDirectory(backupRoot);
        }
    }

    private static void RecoverInterruptedReplacement(string targetRoot, string packageFingerprint)
    {
        var marker = Path.Combine(targetRoot, PackageMarker);
        var currentPackageWasCommitted = File.Exists(marker)
            && string.Equals(File.ReadAllText(marker).Trim(), packageFingerprint, StringComparison.OrdinalIgnoreCase);

        var backupRoots = Directory.GetDirectories(targetRoot, $"{BackupDirectoryPrefix}*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToArray();
        if (currentPackageWasCommitted)
        {
            foreach (var committedBackupRoot in backupRoots)
                TryDeleteDirectory(committedBackupRoot);
        }
        else
        {
            foreach (var backupRoot in backupRoots)
            {
                var transactionManifest = Path.Combine(backupRoot, TransactionEntriesMarker);
                var originalEntriesManifest = Path.Combine(backupRoot, TransactionOriginalEntriesMarker);
                if (!File.Exists(transactionManifest) || !File.Exists(originalEntriesManifest))
                {
                    // Both manifests are written before any managed entry is moved. An incomplete
                    // transaction directory therefore contains no authoritative backup to restore.
                    TryDeleteDirectory(backupRoot);
                }
                else
                {
                    var entries = File.ReadAllLines(transactionManifest)
                        .Select(entry => entry.Trim())
                        .Where(IsSafeTopLevelEntry)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var originalEntries = File.ReadAllLines(originalEntriesManifest)
                        .Select(entry => entry.Trim())
                        .Where(IsSafeTopLevelEntry)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    RestoreBackup(targetRoot, backupRoot, entries, originalEntries);
                    foreach (var recoveredBackupRoot in backupRoots)
                        TryDeleteDirectory(recoveredBackupRoot);
                    break;
                }
            }
        }

        foreach (var stagingRoot in Directory.GetDirectories(targetRoot, $"{StagingDirectoryPrefix}*", SearchOption.TopDirectoryOnly))
            TryDeleteDirectory(stagingRoot);
    }

    private static void RestoreBackup(
        string targetRoot,
        string backupRoot,
        IEnumerable<string> entries,
        IEnumerable<string> originalEntries)
    {
        var safeEntries = entries
            .Where(IsSafeTopLevelEntry)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var originalEntrySet = originalEntries
            .Where(IsSafeTopLevelEntry)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in safeEntries)
        {
            if (!originalEntrySet.Contains(entry))
            {
                DeleteEntry(Path.Combine(targetRoot, entry));
                continue;
            }

            // If the backup entry is already gone, a previous rollback attempt either restored it
            // or the forward move never happened. Keeping the target makes recovery idempotent.
            if (!EntryExists(backupRoot, entry))
                continue;

            DeleteEntry(Path.Combine(targetRoot, entry));
            MoveEntryIfExists(backupRoot, targetRoot, entry);
        }

        RestoreFileOrDelete(
            Path.Combine(backupRoot, TransactionPreviousEntriesMarker),
            Path.Combine(targetRoot, PackageEntriesMarker));
        RestoreFileOrDelete(
            Path.Combine(backupRoot, TransactionPreviousFingerprintMarker),
            Path.Combine(targetRoot, PackageMarker));
    }

    private static bool EntryExists(string root, string entry)
    {
        var path = Path.Combine(root, entry);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static void CopyFileIfExists(string source, string destination)
    {
        if (File.Exists(source))
            File.Copy(source, destination, overwrite: true);
    }

    private static void RestoreFileOrDelete(string backupPath, string targetPath)
    {
        if (File.Exists(backupPath))
            File.Copy(backupPath, targetPath, overwrite: true);
        else if (File.Exists(targetPath))
            File.Delete(targetPath);
    }

    private static string[] ExtractPackage(Stream packageStream, string targetRoot)
    {
        var topLevelEntries = new HashSet<string>(StringComparer.Ordinal);
        var normalizedRoot = Path.GetFullPath(targetRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            var normalizedEntryName = entry.FullName.Replace('\\', '/').TrimStart('/');
            var pathSegments = normalizedEntryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Any(segment => segment is "." or ".."))
                throw new InvalidDataException($"Package entry contains relative traversal segments: {entry.FullName}");
            var topLevelEntry = normalizedEntryName.Split('/', 2)[0];
            if (string.IsNullOrWhiteSpace(topLevelEntry)
                || topLevelEntry is "." or ".."
                || topLevelEntry.StartsWith(".mfa-package", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Invalid managed package entry: {entry.FullName}");
            }

            topLevelEntries.Add(topLevelEntry);
            var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, normalizedEntryName));
            if (!destinationPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"Package entry escapes the application directory: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = entry.Open();
            using var target = File.Create(destinationPath);
            source.CopyTo(target);
        }

        return topLevelEntries.ToArray();
    }

    private static void ValidateStagedPackage(string stagingRoot)
    {
        if (!File.Exists(Path.Combine(stagingRoot, "interface.json"))
            && !File.Exists(Path.Combine(stagingRoot, "interface.jsonc")))
        {
            throw new InvalidDataException("The embedded Android resource package has no interface.json or interface.jsonc.");
        }
    }

    private static string[] ReadManagedEntries(string targetRoot)
    {
        var manifest = Path.Combine(targetRoot, PackageEntriesMarker);
        if (!File.Exists(manifest))
            return [];

        return File.ReadAllLines(manifest)
            .Select(entry => entry.Trim())
            .Where(IsSafeTopLevelEntry)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> GetLegacyManagedEntries(string targetRoot)
    {
        foreach (var entry in LegacyManagedEntries)
            yield return entry;

        // These patterns exactly mirror root files accepted by the old payload workflow. Do not
        // broaden them: unknown files may belong to the user and are outside bootstrap ownership.
        foreach (var pattern in new[] { "CONTACT*", "LICENSE*", "README*", "requirements*.txt", "*.md" })
        {
            foreach (var file in Directory.EnumerateFiles(targetRoot, pattern, SearchOption.TopDirectoryOnly))
                yield return Path.GetFileName(file);
        }
    }

    private static bool IsSafeTopLevelEntry(string entry) =>
        !string.IsNullOrWhiteSpace(entry)
        && entry is not "." and not ".."
        && !entry.Contains('/')
        && !entry.Contains('\\')
        && !entry.StartsWith(".mfa-package", StringComparison.Ordinal);

    private static void MoveEntryIfExists(string sourceRoot, string destinationRoot, string entry)
    {
        if (!IsSafeTopLevelEntry(entry))
            throw new InvalidDataException($"Unsafe managed package entry: {entry}");

        var source = Path.Combine(sourceRoot, entry);
        if (!File.Exists(source) && !Directory.Exists(source))
            return;

        var destination = Path.Combine(destinationRoot, entry);
        DeleteEntry(destination);
        Directory.CreateDirectory(destinationRoot);
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private static void WriteAllTextAtomically(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MFAAvalonia", $"Unable to remove package transaction directory '{path}': {ex.Message}");
        }
    }

    private static Stream OpenAsset(Context context, string assetName)
    {
        var assets = context.Assets ?? throw new InvalidOperationException("Android AssetManager is unavailable.");
        return assets.Open($"{AssetRoot}/{assetName}");
    }
}
