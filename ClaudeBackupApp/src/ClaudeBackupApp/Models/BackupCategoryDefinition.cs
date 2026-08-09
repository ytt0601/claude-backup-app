namespace ClaudeBackupApp.Models;

public enum SourceKind
{
    File,
    Directory
}

public class BackupSourceEntry
{
    public required string AbsolutePath { get; init; }
    public required SourceKind Kind { get; init; }
}

public class BackupCategoryDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required bool DefaultEnabled { get; init; }
    public required bool ContainsSensitiveData { get; init; }

    /// <summary>ZIP内でこのカテゴリのファイルを格納するルートフォルダ名</summary>
    public required string ZipFolderName { get; init; }

    public required IReadOnlyList<BackupSourceEntry> Sources { get; init; }
}
