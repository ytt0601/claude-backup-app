using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

public class RestoreService
{
    /// <summary>
    /// バックアップZIP内の manifest.json を読み込む。復元処理はこのmanifestのみに依存し、
    /// PathCatalog(現在のアプリのパス定義)には依存しない。将来パス定義が変わっても
    /// 過去のバックアップを元の場所へ正しく復元できるようにするため。
    /// </summary>
    public async Task<BackupManifest> ReadManifestAsync(string zipPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("manifest.jsonが見つかりません。有効なバックアップファイルではない可能性があります。");

        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: cancellationToken);
        return manifest ?? throw new InvalidDataException("manifest.jsonの読み込みに失敗しました。");
    }

    /// <summary>
    /// 選択されたカテゴリのファイルを復元する。上書きされる既存ファイルは復元前に
    /// %LOCALAPPDATA%\ClaudeBackupApp\PreRestoreEvacuation\ 配下へ退避する。
    /// manifest.jsonはバックアップZIPの中身であり(第三者から渡された可能性もあり)信用できないため、
    /// 復元先は PathCatalog.IsAllowedRestoreDestination で正規のClaude関連パス配下であることを
    /// 必ず検証してから書き込む。RootKind/RelativePathが記録されている場合は、現在の環境の
    /// %APPDATA%・%USERPROFILE%を基準に復元先を再解決する(別マシン/別ユーザーアカウントへの復元に対応)。
    /// </summary>
    /// <returns>今回の退避先フォルダ(何も退避しなかった場合でもパスは返す)</returns>
    public async Task<string> RestoreAsync(
        string zipPath,
        BackupManifest manifest,
        IReadOnlyList<string> selectedCategoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var evacuationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeBackupApp", "PreRestoreEvacuation", DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss"));

        using var logger = new OperationLogger("Restore");
        using var archive = ZipFile.OpenRead(zipPath);

        var files = manifest.Categories
            .Where(c => selectedCategoryIds.Contains(c.Id))
            .SelectMany(c => c.Files)
            .ToList();

        var total = files.Count;
        var processedCount = 0;
        var completed = false;

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var destination = PathCatalog.ResolvePortablePath(file.RootKind, file.RelativePath) ?? file.Destination;

                try
                {
                    if (!PathCatalog.IsAllowedRestoreDestination(destination))
                    {
                        logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                            $"スキップ(不正な復元先パス): {destination}");
                        continue;
                    }

                    var entry = archive.GetEntry(file.ZipEntry);
                    if (entry is null)
                    {
                        logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                            $"ZIP内に見つかりません: {file.ZipEntry}");
                        continue;
                    }

                    EvacuateIfExists(destination, evacuationRoot);

                    var destinationDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    entry.ExtractToFile(destination, overwrite: true);

                    logger.Report(progress, index + 1, total, destination, LogLevel.Info, $"復元: {destination}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                        $"スキップ(書き込み不可): {destination} ({ex.Message})");
                }
                finally
                {
                    processedCount = index + 1;
                }
            }

            completed = true;
        }
        finally
        {
            var message = completed
                ? $"リストア完了(退避先: {evacuationRoot})"
                : cancellationToken.IsCancellationRequested
                    ? $"リストアを中断しました(退避先: {evacuationRoot})"
                    : $"リストアがエラーで中断しました(退避先: {evacuationRoot})";

            logger.Report(progress, processedCount, total, string.Empty, LogLevel.Info, message);
        }

        await Task.CompletedTask;
        return evacuationRoot;
    }

    private static void EvacuateIfExists(string destinationPath, string evacuationRoot)
    {
        if (!File.Exists(destinationPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var pathWithoutRoot = fullPath.Length >= 3 && fullPath[1] == ':'
            ? fullPath[3..] // "C:\" を除去
            : fullPath.TrimStart(Path.DirectorySeparatorChar);

        var evacuationPath = Path.Combine(evacuationRoot, pathWithoutRoot);
        var evacuationDir = Path.GetDirectoryName(evacuationPath);
        if (!string.IsNullOrEmpty(evacuationDir))
        {
            Directory.CreateDirectory(evacuationDir);
        }

        File.Move(destinationPath, evacuationPath, overwrite: true);
    }
}
