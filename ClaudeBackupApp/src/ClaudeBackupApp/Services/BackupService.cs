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
    /// 機微情報カテゴリ(認証情報等)の対象ファイルが1件でも実在する場合は、共有時に気づけるよう
    /// ZIP作成時点でファイル名にマーカーを付与する(作成後にリネームする方式は、リネーム自体が
    /// 失敗した場合にマーカーが付かないまま残る失敗モードを生むため採らない)。
    /// ファイル列挙・ZIP書き込みはいずれも同期APIを含むため、UIスレッドをブロックしないよう
    /// 全体をバックグラウンドスレッドで実行する。
    /// </summary>
    /// <returns>作成されたZIPファイルの絶対パス</returns>
    public Task<string> CreateBackupAsync(
        IReadOnlyList<string> selectedCategoryIds,
        string destinationFolder,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => CreateBackupCoreAsync(selectedCategoryIds, destinationFolder, progress, cancellationToken), cancellationToken);
    }

    private static async Task<string> CreateBackupCoreAsync(
        IReadOnlyList<string> selectedCategoryIds,
        string destinationFolder,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationFolder);

        var timestamp = DateTimeOffset.Now;

        using var logger = new OperationLogger("Backup");

        var categories = PathCatalog.Categories
            .Where(c => selectedCategoryIds.Contains(c.Id))
            .ToList();

        var enumerationWarnings = new List<string>();
        var plan = categories
            .SelectMany(category => PathCatalog.EnumerateExistingFiles(category, enumerationWarnings.Add)
                .Select(f => (Category: category, f.AbsolutePath, f.ZipEntry)))
            .ToList();

        // 機微情報を含むカテゴリの対象ファイルが実在するかどうかは、ZIPを開く前のこの時点で判定できる。
        var hasSensitiveSource = plan.Any(p => p.Category.ContainsSensitiveData);
        var zipFileNameSuffix = hasSensitiveSource ? "_Credentials" : string.Empty;
        var zipPath = Path.Combine(destinationFolder, $"ClaudeBackup_{timestamp:yyyyMMdd_HHmmss}{zipFileNameSuffix}.zip");

        var manifestCategories = categories.ToDictionary(
            c => c.Id,
            c => new BackupManifestCategory
            {
                Id = c.Id,
                DisplayName = c.DisplayName,
                ContainsSensitiveData = c.ContainsSensitiveData,
                DefaultEnabled = c.DefaultEnabled,
            });

        var plannedCountByCategory = plan
            .GroupBy(p => p.Category.Id)
            .ToDictionary(g => g.Key, g => g.Count());

        try
        {
            await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var total = plan.Count;

                foreach (var warning in enumerationWarnings)
                {
                    logger.Report(progress, 0, total, string.Empty, LogLevel.Warning, warning);
                }

                if (total == 0)
                {
                    logger.Report(progress, 0, 0, string.Empty, LogLevel.Warning,
                        "選択したカテゴリに対象ファイルが1件も見つかりませんでした。中身が空のバックアップが作成されます。");
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

                var manifest = new BackupManifest
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
        }
        catch (Exception)
        {
            // キャンセル・I/Oエラーいずれの場合も、作成途中の壊れたZIPを残さない。
            TryDeleteFile(zipPath);
            throw;
        }

        logger.Report(progress, plan.Count, plan.Count, zipPath, LogLevel.Info, $"バックアップ完了: {zipPath}");

        return zipPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後始末が失敗しても、呼び出し元での例外の再送出は継続する
        }
    }
}
