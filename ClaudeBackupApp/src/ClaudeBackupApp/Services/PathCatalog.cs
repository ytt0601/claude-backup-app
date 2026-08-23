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
    /// </summary>
    public static bool IsAllowedRestoreDestination(string destinationPath)
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

        var claudeAppData = ClaudeAppDataDir + Path.DirectorySeparatorChar;
        var claudeHome = ClaudeHomeDir + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(claudeAppData, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(claudeHome, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, ClaudeJsonPath, StringComparison.OrdinalIgnoreCase);
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
