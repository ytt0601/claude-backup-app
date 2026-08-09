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

    public static IReadOnlyList<BackupCategoryDefinition> Categories => BuildCategories();

    private static IReadOnlyList<BackupCategoryDefinition> BuildCategories()
    {
        var claudeAppData = Path.Combine(AppDataDir, "Claude");
        var claudeHome = Path.Combine(UserProfileDir, ".claude");

        return new[]
        {
            new BackupCategoryDefinition
            {
                Id = "desktop-settings",
                DisplayName = "Claude Desktop設定",
                DefaultEnabled = true,
                ContainsSensitiveData = false,
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
                ContainsSensitiveData = false,
                ZipFolderName = "CodeSettings",
                Sources = new[]
                {
                    FileEntry(Path.Combine(UserProfileDir, ".claude.json")),
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
        "Network", "Crashpad", "Partitions", "DIPS", "SharedStorage", "blob_storage", "claude-code",
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
    /// フォルダの列挙自体が権限不足等で失敗した場合は、そのソースだけをスキップし
    /// (<paramref name="onWarning"/>で通知したうえで)他のソースの処理は継続する。
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

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(source.AbsolutePath, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onWarning?.Invoke($"スキップ(フォルダ読み取り不可): {source.AbsolutePath} ({ex.Message})");
                continue;
            }

            foreach (var file in files)
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

        var claudeAppData = Path.Combine(AppDataDir, "Claude") + Path.DirectorySeparatorChar;
        var claudeHome = Path.Combine(UserProfileDir, ".claude") + Path.DirectorySeparatorChar;
        var claudeJson = Path.Combine(UserProfileDir, ".claude.json");

        return fullPath.StartsWith(claudeAppData, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(claudeHome, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, claudeJson, StringComparison.OrdinalIgnoreCase);
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
