using System.IO;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

/// <summary>
/// バックアップ対象カテゴリと実体パスの定義。
/// 要件定義書 4章「バックアップ対象カテゴリと実体パス」に対応する。
/// リストアはこのカタログに依存せず manifest.json のみで完結する(将来パス定義が変わっても
/// 過去のバックアップを復元できるようにするため)。
/// </summary>
public static class PathCatalog
{
    private static string AppDataDir => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string UserProfileDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// このアプリが読み書きしてよいClaude関連ルートパス。BuildCategories(バックアップ対象パスの定義)と
    /// IsAllowedRestoreDestination(リストア時の書き込み許可判定)の両方がこの一箇所を参照することで、
    /// 新しいバックアップ対象を追加した際に許可判定側の更新を忘れて復元不能になる事故を防ぐ。
    /// </summary>
    private static string ClaudeAppDataDir => Path.Combine(AppDataDir, "Claude");
    private static string ClaudeHomeDir => Path.Combine(UserProfileDir, ".claude");
    private static string ClaudeJsonPath => Path.Combine(UserProfileDir, ".claude.json");

    public static IReadOnlyList<BackupCategoryDefinition> Categories => BuildCategories();

    private static IReadOnlyList<BackupCategoryDefinition> BuildCategories()
    {
        var claudeAppData = ClaudeAppDataDir;
        var claudeHome = ClaudeHomeDir;

        return new[]
        {
            new BackupCategoryDefinition
            {
                Id = "desktop-settings",
                DisplayName = "Claude Desktop設定",
                DefaultEnabled = true,
                ContainsSensitiveData = true,
                ZipFolderName = "DesktopSettings",
                Sources = new[]
                {
                    FileEntry(Path.Combine(claudeAppData, "claude_desktop_config.json")),
                    FileEntry(Path.Combine(claudeAppData, "config.json")),
                    FileEntry(Path.Combine(claudeAppData, "cowork-enabled-cli-ops.json")),
                }
            },
            new BackupCategoryDefinition
            {
                Id = "code-settings",
                DisplayName = "Claude Code設定",
                DefaultEnabled = true,
                ContainsSensitiveData = true,
                ZipFolderName = "CodeSettings",
                Sources = new[]
                {
                    FileEntry(ClaudeJsonPath),
                    FileEntry(Path.Combine(claudeHome, "settings.json")),
                    DirEntry(Path.Combine(claudeHome, "plugins")),
                    DirEntry(Path.Combine(claudeHome, "skills")),
                }
            },
            new BackupCategoryDefinition
            {
                Id = "history",
                DisplayName = "プロジェクト・会話履歴",
                DefaultEnabled = true,
                ContainsSensitiveData = false,
                ZipFolderName = "History",
                Sources = new[]
                {
                    DirEntry(Path.Combine(claudeHome, "projects")),
                    DirEntry(Path.Combine(claudeHome, "sessions")),
                    FileEntry(Path.Combine(claudeHome, "history.jsonl")),
                    DirEntry(Path.Combine(claudeHome, "tasks")),
                    DirEntry(Path.Combine(claudeHome, "plans")),
                    DirEntry(Path.Combine(claudeAppData, "claude-code-sessions")),
                }
            },
            new BackupCategoryDefinition
            {
                Id = "credentials",
                DisplayName = "認証情報・トークン",
                DefaultEnabled = false,
                ContainsSensitiveData = true,
                ZipFolderName = "Credentials",
                Sources = new[]
                {
                    FileEntry(Path.Combine(claudeHome, ".credentials.json")),
                    FileEntry(Path.Combine(claudeAppData, "buddy-tokens.json")),
                    FileEntry(Path.Combine(claudeAppData, "ant-did")),
                }
            },
        };
    }

    private static BackupSourceEntry FileEntry(string path) => new() { AbsolutePath = path, Kind = SourceKind.File };
    private static BackupSourceEntry DirEntry(string path) => new() { AbsolutePath = path, Kind = SourceKind.Directory };

    /// <summary>
    /// 要件定義書 4章「除外対象」に列挙されているキャッシュ・診断ログ・再生成可能なデータのフォルダ名。
    /// 現状はSourcesの対象パス外に位置するため実害はないが、将来Claude側の内部構成が変わり
    /// これらがSources配下にネストされても黙って取り込まれないよう、列挙時に明示的に除外する。
    /// </summary>
    private static readonly string[] ExcludedDirectoryNames =
    {
        // %APPDATA%\Claude\ 配下
        "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "IndexedDB", "Local Storage", "Session Storage", "Shared Dictionary", "WebStorage",
        "Network", "Crashpad", "Partitions", "DIPS", "DIPS-wal", "SharedStorage", "SharedStorage-wal",
        "blob_storage", "claude-code",
        // %USERPROFILE%\.claude\ 配下
        "cache", "chrome", "downloads", "paste-cache", "backups", "session-env",
        "debug", "telemetry", "shell-snapshots",
    };

    private static bool IsExcludedRelativePath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            foreach (var excluded in ExcludedDirectoryNames)
            {
                if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// カテゴリ内の実在するファイルを (絶対パス, ZIPエントリ名) の組として列挙する。
    /// 存在しないファイル・フォルダは黙ってスキップする(未使用機能などで未生成の場合があるため)。
    /// フォルダの列挙自体が権限不足等で失敗した場合は、その原因となった個別のサブフォルダだけを
    /// スキップし(<paramref name="onWarning"/>で通知したうえで)、兄弟フォルダの正常なファイルは
    /// 引き続き列挙する。
    /// </summary>
    public static IEnumerable<(string AbsolutePath, string ZipEntry)> EnumerateExistingFiles(
        BackupCategoryDefinition category, Action<string>? onWarning = null)
    {
        foreach (var source in category.Sources)
        {
            if (source.Kind == SourceKind.File)
            {
                if (File.Exists(source.AbsolutePath))
                {
                    var fileName = Path.GetFileName(source.AbsolutePath);
                    yield return (source.AbsolutePath, $"{category.ZipFolderName}/{fileName}");
                }
                continue;
            }

            if (!Directory.Exists(source.AbsolutePath))
            {
                continue;
            }

            var dirName = Path.GetFileName(source.AbsolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            foreach (var file in EnumerateFilesResilient(source.AbsolutePath, onWarning))
            {
                var relative = Path.GetRelativePath(source.AbsolutePath, file).Replace('\\', '/');
                if (IsExcludedRelativePath(relative))
                {
                    continue;
                }

                yield return (file, $"{category.ZipFolderName}/{dirName}/{relative}");
            }
        }
    }

    /// <summary>
    /// Directory.EnumerateFiles(path, "*", AllDirectories)はサブフォルダ1つでも読み取り不可だと
    /// 例外を送出しそれまで列挙できていたファイルごと失われるため、フォルダ単位で手動再帰し、
    /// 権限エラー等が起きたサブフォルダだけをスキップして兄弟フォルダの列挙を継続する。
    /// </summary>
    private static IEnumerable<string> EnumerateFilesResilient(string rootPath, Action<string>? onWarning)
    {
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(rootPath);

        while (pendingDirs.Count > 0)
        {
            var currentDir = pendingDirs.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onWarning?.Invoke($"スキップ(フォルダ読み取り不可): {currentDir} ({ex.Message})");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onWarning?.Invoke($"スキップ(フォルダ読み取り不可): {currentDir} ({ex.Message})");
                continue;
            }

            foreach (var subDir in subDirs)
            {
                pendingDirs.Push(subDir);
            }
        }
    }

    /// <summary>
    /// 復元先パスが正規のClaude関連ルート(%APPDATA%\Claude, %USERPROFILE%\.claude,
    /// %USERPROFILE%\.claude.json)配下かどうかを検証する。
    /// manifest.jsonはユーザーが選択した(=第三者から渡された可能性もある)バックアップZIPの中身であり
    /// 信用できないため、リストア時に任意パスへ書き込まれることを防ぐガードとして使用する。
    ///
    /// Path.GetFullPath による正規化("..\"の除去)だけでは、途中の階層がシンボリックリンクや
    /// ディレクトリジャンクション(Windowsでは非管理者でも mklink /J で作成可能)だった場合に
    /// 実際の書き込み先が許可範囲外へ逃げるため、リンクを解決した実パスでも同じ判定を行う。
    /// </summary>
    /// <param name="linkResolutionCache">
    /// リンク解決はパスの階層数ぶんファイルシステムを叩くため、同一リストア内で使い回すキャッシュ。
    /// 省略した場合は都度解決する(単発の判定用)。
    /// </param>
    public static bool IsAllowedRestoreDestination(
        string destinationPath, IDictionary<string, string>? linkResolutionCache = null)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(destinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!IsUnderAllowedRoot(fullPath))
        {
            return false;
        }

        // 正規化後のパスが許可範囲でも、途中のリンクで実体が範囲外を指していれば拒否する。
        var realPath = ResolveLinkChain(fullPath, linkResolutionCache);
        return IsUnderAllowedRoot(realPath);
    }

    private static bool IsUnderAllowedRoot(string fullPath)
    {
        var claudeAppData = ClaudeAppDataDir + Path.DirectorySeparatorChar;
        var claudeHome = ClaudeHomeDir + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(claudeAppData, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(claudeHome, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, ClaudeJsonPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// パスをルートから1階層ずつ辿り、シンボリックリンク・ジャンクションを最終ターゲットまで
    /// 解決した実パスを返す。まだ存在しない階層(これから作る復元先など)はそのまま連結する。
    /// 途中階層を1つずつ見るのは、FileSystemInfo.ResolveLinkTarget が「そのパス自身」しか
    /// 解決せず、祖先にあるリンクを見逃すため。
    /// </summary>
    private static string ResolveLinkChain(string fullPath, IDictionary<string, string>? cache)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        // ディレクトリ部分だけ解決すればよい(最終要素はこれから作る/置き換えるファイル)。
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return fullPath;
        }

        if (cache is not null && cache.TryGetValue(directory, out var cached))
        {
            return Path.Combine(cached, fileName);
        }

        var segments = directory[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                var target = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target is not null)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 存在しない・権限不足で解決できない階層は、そのままのパスとして扱う
            }
        }

        cache?.TryAdd(directory, current);
        return Path.Combine(current, fileName);
    }

    /// <summary>
    /// 許可された復元先のうち、復元しただけで外部コマンドの起動につながりうるものかどうか。
    /// IsAllowedRestoreDestination は「システム領域を壊さない」ための境界であって、
    /// 許可範囲の中には Claude 自身が起動時に読み込んで外部プロセスを起動する定義
    /// (MCPサーバー定義、hooks、プラグイン/スキルのコード)が含まれる。つまり信用できないZIPの
    /// 復元は実質的に任意コード実行になりうるため、実行前にユーザーへ明示的な警告を出す判断に使う。
    /// 警告が目的なので、判定は迷ったら「リスクあり」に倒す(過検出は許容する)。
    /// </summary>
    public static bool IsCodeExecutionRisk(string destinationPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(destinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // パスとして解釈できないものは復元自体が拒否されるが、判定としては安全側に倒す
            return true;
        }

        // MCPサーバー定義(command/argsで任意の実行ファイルを起動する)を含みうるファイル
        if (string.Equals(fullPath, ClaudeJsonPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(fullPath);
        if (string.Equals(fileName, "claude_desktop_config.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "settings.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // プラグイン・スキルはコードそのもの
        var claudeHome = ClaudeHomeDir + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(claudeHome + "plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(claudeHome + "skills" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 絶対パスを、現在の実行環境非依存な (ルート種別, ルートからの相対パス) に変換する。
    /// バックアップ時に記録しておくことで、別マシン/別ユーザーアカウントへのリストア時にも
    /// 現在の環境の %APPDATA%・%USERPROFILE% を基準に復元先を再解決できるようにする。
    /// </summary>
    public static (string? RootKind, string? RelativePath) ToPortableRoot(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        var appData = AppDataDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var userProfile = UserProfileDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (full.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
        {
            return ("AppData", Path.GetRelativePath(AppDataDir, full).Replace('\\', '/'));
        }

        if (full.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return ("UserProfile", Path.GetRelativePath(UserProfileDir, full).Replace('\\', '/'));
        }

        return (null, null);
    }

    /// <summary>
    /// <see cref="ToPortableRoot"/>で記録した (ルート種別, 相対パス) を、現在の環境の
    /// %APPDATA%・%USERPROFILE% を基準に絶対パスへ復元する。旧形式のバックアップ等で
    /// ルート種別・相対パスが記録されていない場合はnullを返す(呼び出し側でDestinationにフォールバックする)。
    /// </summary>
    public static string? ResolvePortablePath(string? rootKind, string? relativePath)
    {
        if (string.IsNullOrEmpty(rootKind) || string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        var root = rootKind switch
        {
            "AppData" => AppDataDir,
            "UserProfile" => UserProfileDir,
            _ => null,
        };

        return root is null ? null : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
