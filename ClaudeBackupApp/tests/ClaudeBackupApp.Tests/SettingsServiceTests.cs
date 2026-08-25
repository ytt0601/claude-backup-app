using ClaudeBackupApp.Services;
using Xunit;

namespace ClaudeBackupApp.Tests;

/// <summary>
/// バックアップ保存先の危険度判定。
/// バックアップZIPは暗号化されず認証情報を平文で含みうるため、
/// 外部へ複製されうる場所を検出して警告する。
/// </summary>
public class SettingsServiceTests
{
    /// <summary>
    /// 既定の保存先そのものが警告対象になってはならない。
    /// 既定をドキュメントフォルダから %LOCALAPPDATA% へ移したのはこのため。
    /// </summary>
    [Fact]
    public void 既定の保存先は危険と判定されない()
    {
        Assert.Equal(BackupFolderRisk.None, SettingsService.EvaluateBackupFolder(SettingsService.DefaultBackupFolder));
    }

    [Fact]
    public void 既定の保存先はローカルアプリデータ配下にある()
    {
        Assert.StartsWith(
            TestPaths.LocalAppData,
            SettingsService.DefaultBackupFolder,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\server\share\backups")]
    [InlineData(@"\\?\UNC\server\share\backups")]
    public void UNCパスはローカルドライブ外として判定する(string folder)
    {
        Assert.False(SettingsService.IsLocalFixedPath(folder));
        Assert.Equal(BackupFolderRisk.NotLocalDrive, SettingsService.EvaluateBackupFolder(folder));
    }

    [Fact]
    public void ローカルの固定ドライブ上のパスは許容する()
    {
        Assert.True(SettingsService.IsLocalFixedPath(TestPaths.LocalAppData));
    }

    [Theory]
    [InlineData("OneDrive")]
    [InlineData("Dropbox")]
    [InlineData("Google Drive")]
    [InlineData("Box")]
    [InlineData("Nextcloud")]
    public void ユーザープロファイル直下の既知の同期フォルダを検出する(string syncFolderName)
    {
        var folder = Path.Combine(TestPaths.UserProfile, syncFolderName, "ClaudeBackups");

        Assert.True(SettingsService.IsCloudSyncedPath(folder), $"検出できていない: {folder}");
        Assert.Equal(BackupFolderRisk.CloudSynced, SettingsService.EvaluateBackupFolder(folder));
    }

    /// <summary>
    /// OneDriveが組織名付きで作るフォルダ("OneDrive - Contoso")も検出対象。
    /// </summary>
    [Fact]
    public void 組織名付きの同期フォルダを検出する()
    {
        var folder = Path.Combine(TestPaths.UserProfile, "OneDrive - Contoso", "ClaudeBackups");

        Assert.True(SettingsService.IsCloudSyncedPath(folder));
    }

    /// <summary>
    /// 前方一致だけで判定すると、名前が似ているだけの無関係なフォルダまで警告してしまう。
    /// 区切りとして " - " を要求している。
    /// </summary>
    [Theory]
    [InlineData("BoxBackups")]
    [InlineData("OneDriveArchive")]
    [InlineData("Documents")]
    public void 名前が似ているだけのフォルダは同期フォルダと判定しない(string folderName)
    {
        var folder = Path.Combine(TestPaths.UserProfile, folderName, "ClaudeBackups");

        Assert.False(SettingsService.IsCloudSyncedPath(folder), $"誤検出している: {folder}");
    }

    [Fact]
    public void ユーザープロファイル外のパスは同期フォルダと判定しない()
    {
        Assert.False(SettingsService.IsCloudSyncedPath(@"C:\Backups\Claude"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a\0b")]
    public void パスとして解釈できない入力でも例外を投げない(string folder)
    {
        var exception = Record.Exception(() => SettingsService.EvaluateBackupFolder(folder));

        Assert.Null(exception);
    }
}
