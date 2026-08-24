using System.IO;
using System.Text.Json;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

/// <summary>
/// 本アプリ自身の設定(既定バックアップ保存先など)の永続化。
/// Claudeのデータとは別に %APPDATA%\ClaudeBackupApp\ 配下に保存する。
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeBackupApp", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 既定のバックアップ保存先。バックアップZIPは暗号化されず認証情報・APIキーを平文で含むため、
    /// ドキュメントフォルダ(OneDrive等の同期対象になっていることが多い)ではなく
    /// %LOCALAPPDATA% 配下を既定にする。LocalApplicationDataはローミング/同期の対象外。
    /// ユーザーが明示的に変更した場合はその値を尊重する(<see cref="Load"/>)。
    /// </summary>
    public static string DefaultBackupFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeBackupApp", "Backups");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null
                    && !string.IsNullOrWhiteSpace(settings.DefaultBackupFolder)
                    && IsSafeBackupFolder(settings.DefaultBackupFolder))
                {
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 設定ファイルが壊れている場合は既定値にフォールバックする
        }

        return new AppSettings { DefaultBackupFolder = DefaultBackupFolder };
    }

    /// <summary>
    /// 設定ファイルから読み込んだ保存先が、そのまま使ってよいものかを検証する。
    /// settings.json は同一ユーザー権限の別プロセスから書き換え可能であり、ここに
    /// UNCパス(<c>\\host\share</c>)を仕込まれると、以降のバックアップが認証情報ごと
    /// 外部の共有へ黙って出力されてしまう。本アプリは完全ローカル動作(要件2章)が前提なので、
    /// リモートを指す保存先は採用せず既定値へ戻す。
    /// なお、ユーザーがフォルダ選択ダイアログで明示的に選んだ場合は
    /// <see cref="IsLocalFixedPath"/> の判定結果を警告として提示したうえで尊重する。
    /// </summary>
    public static bool IsSafeBackupFolder(string folder)
    {
        return IsLocalFixedPath(folder);
    }

    /// <summary>
    /// パスがローカルドライブ上の絶対パスかどうか(UNCパスやネットワークドライブでないか)。
    /// </summary>
    public static bool IsLocalFixedPath(string folder)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // UNCパス(\\server\share, \\?\UNC\...)を除外する
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            // ネットワークドライブ(割り当て済みドライブレター)も除外する。
            // ドライブ情報を取得できない場合は判断材料が無いため、安全側に倒して不採用とする。
            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
