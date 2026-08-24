using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

public class RestoreService
{
    /// <summary>
    /// 信用できないZIPによるディスク枯渇(zip bomb)を防ぐための上限。
    /// ZIPヘッダ上の非圧縮サイズは偽装できるため、宣言値ではなく実際に書き出したバイト数で打ち切る。
    /// Claudeの設定・履歴ファイルの実サイズから見て十分な余裕を取った値。
    /// </summary>
    private const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>1回のリストア全体で展開してよい合計バイト数。</summary>
    private const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>manifest.jsonが宣言してよい復元ファイル件数の上限。</summary>
    private const int MaxRestoreFileCount = 100_000;

    /// <summary>
    /// manifest.json 自体の展開バイト数の上限。件数上限(<see cref="MaxRestoreFileCount"/>)は
    /// JSONを最後まで読み終えないと判定できないため、それだけではmanifestをzip bomb化されたときに
    /// 件数チェックへ到達する前にメモリを食い潰される。読み込み段階でバイト数を先に打ち切ること。
    /// 値は「MaxRestoreFileCount件 × 1件あたりのJSONサイズ(長いパス2本を含めても1KB弱)」に
    /// 余裕を見たもので、正当なバックアップのmanifestがこの上限に触れることはない。
    /// </summary>
    private const long MaxManifestBytes = 128L * 1024 * 1024;

    /// <summary>
    /// 退避フォルダ(PreRestoreEvacuation)の保持世代数。退避された元ファイルには認証情報が
    /// 平文で含まれうるため、無期限に溜め続けない。直近数回分は復旧手段として残す。
    /// </summary>
    private const int EvacuationGenerationsToKeep = 5;

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

        // JsonSerializerへ直接エントリのストリームを渡すと、manifest.json 自体が
        // zip bomb化されていた場合に上限なくメモリへ展開されてしまう。先にバイト数を
        // 打ち切りながら読み込み、その結果に対してのみJSONを解析する。
        await using var stream = entry.Open();
        using var buffered = await ReadWithLimitAsync(stream, MaxManifestBytes, cancellationToken);

        // 逆シリアライズ「前」に件数を確かめる。バイト上限だけでは、1バイトあたりの
        // オブジェクト数までは縛れない(128MBのJSONからは数百万件のエントリを作れる)ため、
        // オブジェクト数を数えてから解析しないと MaxRestoreFileCount がメモリの上限として働かない。
        EnsureObjectCountWithinLimit(buffered.GetBuffer().AsSpan(0, (int)buffered.Length));

        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(buffered, cancellationToken: cancellationToken);
        if (manifest is null || manifest.Categories is null)
        {
            throw new InvalidDataException("manifest.jsonの内容が不正です。有効なバックアップファイルではない可能性があります。");
        }

        // 第三者由来のZIPで "Files" が null に細工されている可能性があるため、
        // 復元処理側で null 前提の操作(SelectMany等)が未処理例外を起こさないよう正規化する。
        var declaredFileCount = 0;
        foreach (var category in manifest.Categories)
        {
            category.Files ??= new List<BackupManifestFile>();
            declaredFileCount += category.Files.Count;
        }

        // 上のオブジェクト数チェックを通っていれば必ず上限内に収まるが、
        // 「復元対象の件数」という本来の意味での上限をここでも明示しておく。
        if (declaredFileCount > MaxRestoreFileCount)
        {
            throw new InvalidDataException(
                $"manifest.jsonのファイル件数({declaredFileCount:N0}件)が上限({MaxRestoreFileCount:N0}件)を超えています。" +
                "有効なバックアップファイルではない可能性があります。");
        }

        return manifest;
    }

    /// <summary>
    /// JSON中のオブジェクト数が上限を超えていないかを、逆シリアライズせずに数えて確かめる。
    /// Utf8JsonReader はトークンを走査するだけでオブジェクトを作らないため、
    /// ここで打ち切れば「解析中に大量のオブジェクトが確保される」経路を塞げる。
    /// ルート・カテゴリのぶんは <see cref="MaxRestoreFileCount"/> に対する余裕として上乗せする。
    /// </summary>
    private static void EnsureObjectCountWithinLimit(ReadOnlySpan<byte> utf8Json)
    {
        const int structuralAllowance = 1024;
        var limit = MaxRestoreFileCount + structuralAllowance;

        var reader = new Utf8JsonReader(utf8Json);
        var objectCount = 0;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    continue;
                }

                if (++objectCount > limit)
                {
                    throw new InvalidDataException(
                        $"manifest.jsonの要素数が上限({MaxRestoreFileCount:N0}件)を超えています。" +
                        "有効なバックアップファイルではない可能性があります。");
                }
            }
        }
        catch (JsonException ex)
        {
            // ここで構文エラーが分かった場合も、後続の逆シリアライズと同じ扱いにする
            throw new InvalidDataException(
                $"manifest.jsonの内容が不正です。有効なバックアップファイルではない可能性があります。({ex.Message})", ex);
        }
    }

    /// <summary>
    /// ストリームを上限バイト数までメモリへ読み込む。上限を超えた時点で読み込みを打ち切り、
    /// 例外を送出する(信用できないZIPのエントリを無制限に展開しないため)。
    /// </summary>
    private static async Task<MemoryStream> ReadWithLimitAsync(
        Stream source, long limit, CancellationToken cancellationToken)
    {
        var buffered = new MemoryStream();
        try
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffered.Length + read > limit)
                {
                    throw new InvalidDataException(
                        $"manifest.jsonのサイズが上限({limit:N0} バイト)を超えています。" +
                        "有効なバックアップファイルではない可能性があります。");
                }

                buffered.Write(chunk, 0, read);
            }
        }
        catch
        {
            buffered.Dispose();
            throw;
        }

        buffered.Position = 0;
        return buffered;
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
    /// <param name="onEvacuationUpdated">
    /// 退避先フォルダと「実際に退避したまま残っているファイルの件数」が変化するたびに
    /// (退避に成功したとき、および復元失敗で退避ファイルを元の場所へ戻したとき)呼ばれる。
    /// 1件も退避しなかった場合は一度も呼ばれない。
    /// キャンセル・例外で終わった場合こそ「退避した元ファイルがどこにあるか」の案内が要るため、
    /// 戻り値ではなく処理中のコールバックで渡す。
    /// 件数を併せて渡すのは、退避先フォルダが退避の成否によらず作成されうる
    /// (<see cref="EvacuateIfExists"/> が File.Move の前に親フォルダを作る)ため、
    /// フォルダの実在では「退避したファイルがあるか」を判断できないから。
    /// バックグラウンドスレッドから呼ばれる点に注意。
    /// </param>
    public Task RestoreAsync(
        string zipPath,
        BackupManifest manifest,
        IReadOnlyList<string> selectedCategoryIds,
        IProgress<OperationProgress>? progress,
        Action<string, int>? onEvacuationUpdated,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => RestoreCore(zipPath, manifest, selectedCategoryIds, progress, onEvacuationUpdated, cancellationToken),
            cancellationToken);
    }

    private static void RestoreCore(
        string zipPath,
        BackupManifest manifest,
        IReadOnlyList<string> selectedCategoryIds,
        IProgress<OperationProgress>? progress,
        Action<string, int>? onEvacuationUpdated,
        CancellationToken cancellationToken)
    {
        var evacuationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeBackupApp", "PreRestoreEvacuation", DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss"));

        // 退避フォルダに元ファイルが残っている件数。退避に成功したら増やし、復元失敗で元の場所へ
        // 戻したら減らす。呼び出し元への案内は「フォルダが作られたか」ではなくこの件数で判断する。
        var evacuatedCount = 0;
        void ReportEvacuation() => onEvacuationUpdated?.Invoke(evacuationRoot, evacuatedCount);

        using var logger = new OperationLogger("Restore");
        using var archive = ZipFile.OpenRead(zipPath);

        // archive.GetEntry(string) はエントリ数分の線形探索になるため、ファイル数が多いリストアで
        // O(n^2) にならないよう、事前に名前引きの辞書を作っておく。
        // (同名エントリが複数ある場合は GetEntry と同じく「最初に見つかったもの」を優先する。)
        var entriesByName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in archive.Entries)
        {
            entriesByName.TryAdd(e.FullName, e);
        }

        // manifest.json は信用できないため、複数のファイルが同じ復元先を指すよう細工されている
        // 可能性がある。退避先の一意化だけでは復元先本体への上書きは防げないため、同一リストア内で
        // 既に復元済みの復元先は無警告で上書きせず、最初の1件のみを復元してスキップする。
        // (実際に File.Move が成功した後にのみ追加すること。失敗した1件目のために復元先を
        // 「消費」してしまうと、復元できたはずの2件目まで巻き添えでスキップされてしまう。)
        var restoredDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 復元先検証のリンク解決は階層数ぶんファイルシステムを叩くため、同一リストア内で使い回す。
        // (静的キャッシュにすると検証と書き込みの間隔が開きTOCTOUが広がるので、実行ごとに作る。)
        var linkResolutionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var extractionBudget = new ExtractionBudget(MaxTotalBytes);

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
                    if (!PathCatalog.IsAllowedRestoreDestination(destination, linkResolutionCache))
                    {
                        logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                            $"スキップ(不正な復元先パス): {destination}");
                        continue;
                    }

                    var fullDestination = Path.GetFullPath(destination);
                    if (restoredDestinations.Contains(fullDestination))
                    {
                        logger.Report(progress, index + 1, total, destination, LogLevel.Warning,
                            $"スキップ(復元先が重複): {destination} は同一リストア内の別のファイルと復元先が重複しているため、最初の1件のみ復元しました。");
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
                    ExtractWithLimit(entry, tempPath, extractionBudget, cancellationToken);

                    var evacuatedPath = EvacuateIfExists(destination, evacuationRoot);
                    if (evacuatedPath is not null)
                    {
                        evacuatedCount++;
                        ReportEvacuation();
                    }

                    try
                    {
                        File.Move(tempPath, destination, overwrite: true);
                        tempPath = null;
                        restoredDestinations.Add(fullDestination);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        // 最終移動が失敗した場合、直前に退避しておいた元ファイルを復元先へ戻し、
                        // 復元先に新旧どちらのファイルも無い状態を避ける。
                        // (下の外側catchが捕捉する例外種別と揃えておかないと、ここでの復旧が
                        // 行われないまま外側catchに抜けてしまう。)
                        if (evacuatedPath is not null)
                        {
                            try
                            {
                                File.Move(evacuatedPath, destination, overwrite: true);
                                evacuatedCount--;
                                ReportEvacuation();
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
            // 退避が1件も発生していないときに退避先パスを書くと、存在しない(または空の)フォルダに
            // 元ファイルが保全されているかのように読めてしまうため、件数で出し分ける。
            var evacuationNote = evacuatedCount > 0
                ? $"(退避先: {evacuationRoot} / {evacuatedCount}件)"
                : "(退避したファイルはありません)";

            var message = completed
                ? $"リストア完了{evacuationNote}"
                : cancellationToken.IsCancellationRequested
                    ? $"リストアを中断しました{evacuationNote}"
                    : $"リストアがエラーで中断しました{evacuationNote}";

            logger.Report(progress, processedCount, total, string.Empty, LogLevel.Info, message);

            // 世代整理は今回の退避が確定してから行う(先に消すと、今回のリストアが失敗したときに
            // 古い世代まで失われる)。今回分を含めて EvacuationGenerationsToKeep 世代を残す。
            PruneOldEvacuations(evacuationRoot, logger, progress, processedCount, total);
        }
    }

    /// <summary>
    /// リストア1回で展開してよい合計バイト数の残量。
    /// エントリ単位ではなくリストア全体で共有し、<b>展開に失敗したエントリの分も必ず消費させる</b>。
    /// 成功したエントリの分だけを差し引く実装にすると、上限超過でわざと失敗し続けるエントリを
    /// 並べたZIPが合計上限を素通りしてしまい(失敗分は何度でもディスクへ書けてしまう)、
    /// 合計上限が意味を成さなくなる。
    /// </summary>
    private sealed class ExtractionBudget
    {
        public ExtractionBudget(long totalBytes) => Remaining = totalBytes;

        /// <summary>残量。使い切ると0未満にもなりうる(呼び出し側で下限を取る)。</summary>
        public long Remaining { get; private set; }

        public void Consume(long bytes) => Remaining -= bytes;
    }

    /// <summary>
    /// ZIPエントリを上限バイト数まで一時ファイルへ展開する。
    /// ZipArchiveEntry.Length(ヘッダ上の非圧縮サイズ)は信用できないため、宣言値ではなく
    /// 実際に読み出した量で打ち切る(zip bomb対策)。
    /// ExtractToFileと違い最終更新日時が引き継がれないので、明示的に復元する。
    /// </summary>
    private static void ExtractWithLimit(
        ZipArchiveEntry entry, string tempPath, ExtractionBudget budget, CancellationToken cancellationToken)
    {
        var limit = Math.Min(MaxEntryBytes, Math.Max(budget.Remaining, 0));

        long written = 0;
        using (var source = entry.Open())
        using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 読み出した時点で合計予算から差し引く。このあと上限超過で打ち切っても
                // 消費済みとして残すため、失敗を繰り返して合計上限を回避できない。
                written += read;
                budget.Consume(read);

                if (written > limit)
                {
                    throw new InvalidDataException(
                        $"展開サイズが上限を超えました(上限 {limit:N0} バイト)。" +
                        "極端に大きなデータを含むバックアップファイルの可能性があります。");
                }

                target.Write(buffer, 0, read);
            }
        }

        try
        {
            File.SetLastWriteTimeUtc(tempPath, entry.LastWriteTime.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // ZIP側の日時が不正(範囲外)でも復元自体は続行する
        }
    }

    /// <summary>
    /// 退避フォルダ(PreRestoreEvacuation)の古い世代を削除する。
    /// 退避された元ファイルには認証情報が平文で含まれうるため、無期限に溜め続けない。
    /// 直近 <see cref="EvacuationGenerationsToKeep"/> 世代は復旧手段として残す。
    /// 削除は「消えたことに気づけない」失敗モードを避けるため、必ずログへ記録する。
    /// </summary>
    private static void PruneOldEvacuations(
        string currentEvacuationRoot,
        OperationLogger logger,
        IProgress<OperationProgress>? progress,
        int processedCount,
        int total)
    {
        try
        {
            var parent = Path.GetDirectoryName(currentEvacuationRoot);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return;
            }

            // フォルダ名はyyyyMMdd_HHmmssなので、序数の降順が新しい順になる。
            var generations = Directory.GetDirectories(parent)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Skip(EvacuationGenerationsToKeep)
                .ToList();

            foreach (var stale in generations)
            {
                try
                {
                    DeleteDirectoryTree(stale);
                    logger.Report(progress, processedCount, total, stale, LogLevel.Info,
                        $"古い退避フォルダを削除しました(保持{EvacuationGenerationsToKeep}世代): {stale}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Report(progress, processedCount, total, stale, LogLevel.Warning,
                        $"古い退避フォルダを削除できませんでした: {stale} ({ex.Message})");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 世代整理の失敗はリストア本体の成否に影響させない
            logger.Report(progress, processedCount, total, string.Empty, LogLevel.Warning,
                $"退避フォルダの世代整理に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// フォルダを再帰的に削除する。<b>リパースポイント(シンボリックリンク・ジャンクション)は
    /// 中へ辿らず、リンク自体を外す</b>。
    ///
    /// Directory.Delete(recursive: true) を直接使わないのは、退避フォルダが置かれる
    /// %LOCALAPPDATA% が同一ユーザーの別プロセスから書き込み可能だからである。
    /// そこにジャンクションを1つ置かれると Directory.Delete は UnauthorizedAccessException を
    /// 投げて世代整理が恒久的に失敗し、平文の認証情報を含む退避データが消えずに残り続ける
    /// (削除自体はリンク先を辿らないためデータ破壊にはならないが、保持世代数の制限が効かなくなる)。
    /// </summary>
    private static void DeleteDirectoryTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // リンクの実体(リンク先)には触らず、リンクだけを外す。
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteDirectoryTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
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
