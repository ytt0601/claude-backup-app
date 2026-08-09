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
    /// ZIPの展開・解析はいずれも同期APIのため、UIスレッドをブロックしないよう
    /// 全体をバックグラウンドスレッドで実行する。
    /// </summary>
    public Task<BackupManifest> ReadManifestAsync(string zipPath, CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadManifestCoreAsync(zipPath, cancellationToken), cancellationToken);
    }

    private static async Task<BackupManifest> ReadManifestCoreAsync(string zipPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("manifest.jsonが見つかりません。有効なバックアップファイルではない可能性があります。");

        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: cancellationToken);
        if (manifest is null || manifest.Categories is null)
        {
            throw new InvalidDataException("manifest.jsonの内容が不正です。有効なバックアップファイルではない可能性があります。");
        }

        // 第三者由来のZIPで "Files" が null に細工されている可能性があるため、
        // 復元処理側で null 前提の操作(SelectMany等)が未処理例外を起こさないよう正規化する。
        foreach (var category in manifest.Categories)
        {
            category.Files ??= new List<BackupManifestFile>();
        }

        return manifest;
    }

    /// <summary>
    /// 選択されたカテゴリのファイルを復元する。上書きされる既存ファイルは復元前に
    /// %LOCALAPPDATA%\ClaudeBackupApp\PreRestoreEvacuation\ 配下へ退避する。
    /// manifest.jsonはバックアップZIPの中身であり(第三者から渡された可能性もあり)信用できないため、
    /// 復元先は PathCatalog.IsAllowedRestoreDestination で正規のClaude関連パス配下であることを
    /// 必ず検証してから書き込む。RootKind/RelativePathが記録されている場合は、現在の環境の
    /// %APPDATA%・%USERPROFILE%を基準に復元先を再解決する(別マシン/別ユーザーアカウントへの復元に対応)。
    /// ファイルI/Oはすべて同期APIのため、UIスレッドをブロックしないよう全体をバックグラウンドスレッドで実行する。
    /// </summary>
    /// <returns>今回の退避先フォルダ(何も退避しなかった場合でもパスは返す)</returns>
    public Task<string> RestoreAsync(
        string zipPath,
        BackupManifest manifest,
        IReadOnlyList<string> selectedCategoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => RestoreCore(zipPath, manifest, selectedCategoryIds, progress, cancellationToken), cancellationToken);
    }

    private static string RestoreCore(
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

        // archive.GetEntry(string) はエントリ数分の線形探索になるため、ファイル数が多いリストアで
        // O(n^2) にならないよう、事前に名前引きの辞書を作っておく。
        // (同名エントリが複数ある場合は GetEntry と同じく「後に見つかったもの」を優先する。)
        var entriesByName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in archive.Entries)
        {
            entriesByName[e.FullName] = e;
        }

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
                string? tempPath = null;

                try
                {
                    if (!PathCatalog.IsAllowedRestoreDestination(destination))
                    {
                        logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                            $"スキップ(不正な復元先パス): {destination}");
                        continue;
                    }

                    if (string.IsNullOrEmpty(file.ZipEntry) || !entriesByName.TryGetValue(file.ZipEntry, out var entry))
                    {
                        logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                            $"ZIP内に見つかりません: {file.ZipEntry}");
                        continue;
                    }

                    var destinationDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    // 展開はまず一時ファイルへ行い、成功してから既存ファイルを退避して差し替える。
                    // こうすることでZIPエントリが壊れている等で展開が失敗しても、既存の元ファイルを失わない。
                    tempPath = destination + $".restoring_{Guid.NewGuid():N}.tmp";
                    entry.ExtractToFile(tempPath, overwrite: true);

                    var evacuatedPath = EvacuateIfExists(destination, evacuationRoot);

                    try
                    {
                        File.Move(tempPath, destination, overwrite: true);
                        tempPath = null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 最終移動が失敗した場合、直前に退避しておいた元ファイルを復元先へ戻し、
                        // 復元先に新旧どちらのファイルも無い状態を避ける。
                        if (evacuatedPath is not null)
                        {
                            try
                            {
                                File.Move(evacuatedPath, destination, overwrite: true);
                            }
                            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
                            {
                                logger.Report(progress, index + 1, total, destination, LogLevel.Error,
                                    $"復元先の復旧に失敗しました。元ファイルは退避フォルダに残っています: {evacuatedPath} ({ex2.Message})");
                            }
                        }

                        throw;
                    }

                    logger.Report(progress, index + 1, total, destination, LogLevel.Info, $"復元: {destination}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or PathTooLongException)
                {
                    logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                        $"スキップ(復元失敗): {destination} ({ex.Message})");
                }
                finally
                {
                    if (tempPath is not null)
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 一時ファイルの削除失敗は処理継続を優先し無視する
                        }
                    }

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

        return evacuationRoot;
    }

    /// <summary>
    /// 既存ファイルを退避フォルダへ移動する。退避先のパスは <paramref name="destinationPath"/> から
    /// 決定的に計算されるが、同一リストア内で複数のファイルが同じ復元先を指す(信用できない
    /// manifest.jsonによる細工を含む)場合に備え、既に同名の退避先が存在するときは連番を付けて
    /// 一意な退避先を確保する(先に退避したファイルを上書きして失わないようにするため)。
    /// </summary>
    /// <returns>実際に退避したファイルのパス。退避対象が存在しなかった場合はnull。</returns>
    private static string? EvacuateIfExists(string destinationPath, string evacuationRoot)
    {
        if (!File.Exists(destinationPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var pathWithoutRoot = fullPath.Length >= 3 && fullPath[1] == ':'
            ? fullPath[3..] // "C:\" を除去
            : fullPath.TrimStart(Path.DirectorySeparatorChar);

        var evacuationPath = EnsureUniqueEvacuationPath(Path.Combine(evacuationRoot, pathWithoutRoot));
        var evacuationDir = Path.GetDirectoryName(evacuationPath);
        if (!string.IsNullOrEmpty(evacuationDir))
        {
            Directory.CreateDirectory(evacuationDir);
        }

        File.Move(destinationPath, evacuationPath);
        return evacuationPath;
    }

    private static string EnsureUniqueEvacuationPath(string evacuationPath)
    {
        if (!File.Exists(evacuationPath) && !Directory.Exists(evacuationPath))
        {
            return evacuationPath;
        }

        var dir = Path.GetDirectoryName(evacuationPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(evacuationPath);
        var ext = Path.GetExtension(evacuationPath);

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{fileName}_{i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
