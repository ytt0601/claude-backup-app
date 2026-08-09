namespace ClaudeBackupApp.Models;

public class BackupManifest
{
    public string FormatVersion { get; set; } = "1.0";
    public DateTimeOffset CreatedAt { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<BackupManifestCategory> Categories { get; set; } = new();
}

public class BackupManifestCategory
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool ContainsSensitiveData { get; set; }
    public List<BackupManifestFile> Files { get; set; } = new();
}

public class BackupManifestFile
{
    /// <summary>ZIP内のエントリ名(スラッシュ区切り)</summary>
    public string ZipEntry { get; set; } = string.Empty;

    /// <summary>復元時の絶対パス(バックアップ取得時点の元の場所)。
    /// RootKind/RelativePathが無い(旧形式の)バックアップではこの値をそのまま復元先とする。</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Destinationの基準ルート種別("AppData" または "UserProfile")。
    /// 別マシン・別ユーザーアカウントへのリストア時に、現在の環境を基準として
    /// 復元先を再解決するために使用する。</summary>
    public string? RootKind { get; set; }

    /// <summary>RootKindからの相対パス(スラッシュ区切り)。</summary>
    public string? RelativePath { get; set; }
}
