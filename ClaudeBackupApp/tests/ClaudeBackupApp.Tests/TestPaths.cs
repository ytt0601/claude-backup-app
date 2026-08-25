namespace ClaudeBackupApp.Tests;

/// <summary>
/// テストで使う実環境のルート。
///
/// PathCatalog / SettingsService は static クラスで、%APPDATA% や %USERPROFILE% を
/// Environment.GetFolderPath から直接読む。テストからルートを差し替える手段はないため、
/// テスト側も同じ API で期待値を組み立てて突き合わせる。
/// これらのテストはファイルシステムを読み書きせず、パス文字列の変換・判定のみを対象とする。
/// </summary>
internal static class TestPaths
{
    public static string AppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%APPDATA%\Claude\... </summary>
    public static string ClaudeAppData(params string[] parts) =>
        Path.Combine(new[] { AppData, "Claude" }.Concat(parts).ToArray());

    /// <summary>%USERPROFILE%\.claude\... </summary>
    public static string ClaudeHome(params string[] parts) =>
        Path.Combine(new[] { UserProfile, ".claude" }.Concat(parts).ToArray());

    /// <summary>%USERPROFILE%\.claude.json</summary>
    public static string ClaudeJson => Path.Combine(UserProfile, ".claude.json");
}
