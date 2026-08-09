using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

public class BackupService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 選択されたカテゴリの対象ファイルをZIPにまとめ、manifest.jsonを添えて出力する。
    /// 使用中などで読み取れないファイルはスキップし、警告ログとして報告のうえ処理を継続する。
    /// 機微情報カテゴリ(認証情報等)が1件でも含まれる場合は、共有時に気づけるようZIPファイル名に
    /// マーカーを付与する。
    /// </summary>
    /// <returns>作成されたZIPファイルの絶対パス</returns>
    public async Task<string> CreateBackupAsync(
        IReadOnlyList<string> selectedCategoryIds,
        string destinationFolder,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationFolder);

        var timestamp = DateTimeOffset.Now;
        var zipPath = Path.Combine(destinationFolder, $"ClaudeBackup_{timestamp:yyyyMMdd_HHmmss}.zip");

        using var logger = new OperationLogger("Backup");

        var categories = PathCatalog.Categories
            .Where(c => selectedCategoryIds.Contains(c.Id))
            .ToList();

        var enumerationWarnings = new List<string>();
        var plan = categories
            .SelectMany(category => PathCatalog.EnumerateExistingFiles(category, enumerationWarnings.Add)
                .Select(f => (Category: category, f.AbsolutePath, f.ZipEntry)))
            .ToList();

        var manifestCategories = categories.ToDictionary(
            c => c.Id,
            c => new BackupManifestCategory
            {
                Id = c.Id,
                DisplayName = c.DisplayName,
                ContainsSensitiveData = c.ContainsSensitiveData,
            });

        var plannedCountByCategory = plan
            .GroupBy(p => p.Category.Id)
            .ToDictionary(g => g.Key, g => g.Count());

        BackupManifest manifest;

        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        {
            var total = plan.Count;

            foreach (var warning in enumerationWarnings)
            {
                logger.Report(progress, 0, total, string.Empty, LogLevel.Warning, warning);
            }

            for (var index = 0; index < plan.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (category, absolutePath, zipEntry) = plan[index];

                try
                {
                    await using (var sourceStream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var entry = archive.CreateEntry(zipEntry, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await sourceStream.CopyToAsync(entryStream, cancellationToken);
                    }

                    var (rootKind, relativePath) = PathCatalog.ToPortableRoot(absolutePath);
                    manifestCategories[category.Id].Files.Add(new BackupManifestFile
                    {
                        ZipEntry = zipEntry,
                        Destination = absolutePath,
                        RootKind = rootKind,
                        RelativePath = relativePath,
                    });

                    logger.Report(progress, index + 1, total, absolutePath, LogLevel.Info, $"追加: {absolutePath}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Report(progress, index + 1, total, absolutePath, LogLevel.Warning,
                        $"スキップ(読み取り不可): {absolutePath} ({ex.Message})");
                }
            }

            foreach (var (categoryId, categoryManifest) in manifestCategories)
            {
                var planned = plannedCountByCategory.TryGetValue(categoryId, out var count) ? count : 0;
                if (planned > 0 && categoryManifest.Files.Count == 0)
                {
                    logger.Report(progress, total, total, string.Empty, LogLevel.Warning,
                        $"カテゴリ「{categoryManifest.DisplayName}」は対象ファイルを読み取れなかったため、バックアップに含まれていません。");
                }
            }

            manifest = new BackupManifest
            {
                CreatedAt = timestamp,
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                Categories = manifestCategories.Values.Where(c => c.Files.Count > 0).ToList(),
            };

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, ManifestJsonOptions, cancellationToken);
        }

        var finalZipPath = zipPath;
        if (manifest.Categories.Any(c => c.ContainsSensitiveData))
        {
            var markedZipPath = Path.Combine(destinationFolder, $"ClaudeBackup_{timestamp:yyyyMMdd_HHmmss}_Credentials.zip");
            File.Move(zipPath, markedZipPath, overwrite: true);
            finalZipPath = markedZipPath;
        }

        logger.Report(progress, plan.Count, plan.Count, finalZipPath, LogLevel.Info, $"バックアップ完了: {finalZipPath}");

        return finalZipPath;
    }
}
