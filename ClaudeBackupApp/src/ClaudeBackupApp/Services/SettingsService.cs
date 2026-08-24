using System.IO;
using System.Text.Json;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

/// <summary>
/// バックアップ保存先として指定されたフォルダの危険度。
/// バックアップZIPは暗号化されず認証情報・APIキーを平文で含むため、
/// 「保存した内容が本人の管理外へ複製されうるか」という観点で分類する。
/// </summary>
public enum BackupFolderRisk
{
    /// <summary>ローカルドライブ上で、既知のクラウド同期フォルダ配下でもない。</summary>
    None,

    /// <summary>UNCパスやネットワークドライブなど、ローカルドライブ外。</summary>
    NotLocalDrive,

    /// <summary>ローカルドライブ上だが、クラウド同期フォルダ(OneDrive等)配下。</summary>
    CloudSynced,
}

/// <summary>
/// <see cref="SettingsService.Load"/> の結果。
/// 設定ファイルの保存先を採用しなかった場合に、その値と理由も併せて返す
/// (黙って既定値へ差し替えると、ユーザーが保存先を誤解したまま使い続けてしまうため)。
/// </summary>
/// <param name="Settings">実際に使用する設定。</param>
/// <param name="RejectedFolder">採用しなかった設定ファイル上の保存先。採用した場合はnull。</param>
/// <param name="RejectedReason">採用しなかった理由。<paramref name="RejectedFolder"/>がnullなら意味を持たない。</param>
public sealed record SettingsLoadResult(
    AppSettings Settings, string? RejectedFolder, BackupFolderRisk RejectedReason);

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

    /// <summary>
    /// 設定を読み込む。
    /// settings.json は同一ユーザー権限の別プロセスから書き換え可能であり、ここに
    /// UNCパス(<c>\\host\share</c>)やクラウド同期フォルダを仕込まれると、以降のバックアップが
    /// 認証情報ごと外部へ黙って出力されてしまう。本アプリは完全ローカル動作(要件2章)が前提なので、
    /// <see cref="EvaluateBackupFolder"/> が問題ありと判定した保存先は採用せず既定値へ戻す。
    /// (ユーザーがフォルダ選択ダイアログで明示的に選んだ場合は、同じ判定結果を警告として
    /// 提示したうえで選択を尊重する。設定ファイル由来の値だけをこちらで弾く。)
    ///
    /// 戻した事実は <see cref="SettingsLoadResult.RejectedFolder"/> で呼び出し元へ返す。
    /// 黙って既定値に差し替えると、ユーザーは自分が指定した場所に保存され続けていると
    /// 誤解したまま使い続けてしまうため、必ず画面で知らせること。
    /// </summary>
    public SettingsLoadResult Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.DefaultBackupFolder))
                {
                    var risk = EvaluateBackupFolder(settings.DefaultBackupFolder);
                    if (risk == BackupFolderRisk.None)
                    {
                        return new SettingsLoadResult(settings, null, risk);
                    }

                    return new SettingsLoadResult(
                        new AppSettings { DefaultBackupFolder = DefaultBackupFolder },
                        settings.DefaultBackupFolder,
                        risk);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 設定ファイルが壊れている場合は既定値にフォールバックする
        }

        return new SettingsLoadResult(
            new AppSettings { DefaultBackupFolder = DefaultBackupFolder }, null, BackupFolderRisk.None);
    }

    /// <summary>
    /// 保存先として指定されたフォルダの危険度を判定する。
    /// バックアップZIPは暗号化されず認証情報・APIキーを平文で含むため、
    /// 「ローカルドライブ外」だけでなく「ローカルドライブ上のクラウド同期フォルダ」も検出する。
    /// 後者はドライブ種別では判別できず(OneDrive等はCドライブ上のただのフォルダに見える)、
    /// 既定保存先をドキュメントフォルダから %LOCALAPPDATA% へ移した理由そのものなので、
    /// 手動選択時にも同じ観点で警告できるようにする。
    /// </summary>
    public static BackupFolderRisk EvaluateBackupFolder(string folder)
    {
        if (!IsLocalFixedPath(folder))
        {
            return BackupFolderRisk.NotLocalDrive;
        }

        return IsCloudSyncedPath(folder) ? BackupFolderRisk.CloudSynced : BackupFolderRisk.None;
    }

    /// <summary>
    /// パスがクラウド同期フォルダ配下かどうか。ドライブ種別では判別できないため、
    /// 同期クライアントが設定する環境変数と、%USERPROFILE%直下に作られる既知の同期フォルダ名から
    /// 判定する。網羅は原理的に不可能なので、これは「確実に検出できるものは警告する」ための
    /// ヒューリスティックであり、検出できなかった場合の最後の砦は暗号化されない旨の注意喚起。
    /// </summary>
    public static bool IsCloudSyncedPath(string folder)
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

        // OneDriveは同期ルートを環境変数で公開する(既定以外の場所に置かれていても追える)
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root) && IsUnder(fullPath, root))
            {
                return true;
            }
        }

        // 環境変数を持たない同期クライアント向けに、%USERPROFILE%直下の既知フォルダ名も見る
        var topLevelName = GetNameUnderUserProfile(fullPath);
        if (topLevelName is null)
        {
            return false;
        }

        foreach (var name in KnownSyncFolderNames)
        {
            // 完全一致に加え、OneDriveが作る組織名付きフォルダ("OneDrive - Contoso")も拾う。
            // 前方一致だけにすると "BoxBackups" のような無関係なフォルダまで拾ってしまうため、
            // 区切りとして " - " を要求する。
            if (string.Equals(topLevelName, name, StringComparison.OrdinalIgnoreCase)
                || topLevelName.StartsWith(name + " - ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// %USERPROFILE%直下に作られる代表的なクラウド同期フォルダ名。
    /// </summary>
    private static readonly string[] KnownSyncFolderNames =
    {
        "OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "My Drive",
        "iCloudDrive", "iCloud Drive", "Box", "Box Sync", "Nextcloud", "ownCloud",
        "pCloudDrive", "MEGA", "MEGAsync", "Creative Cloud Files",
    };

    /// <summary>
    /// <paramref name="fullPath"/> が %USERPROFILE% 配下にある場合、その直下の階層名を返す。
    /// (例: C:\Users\x\OneDrive\Backups → "OneDrive")。配下でなければnull。
    /// </summary>
    private static string? GetNameUnderUserProfile(string fullPath)
    {
        var userProfile = Environment
            .GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar);

        var prefix = userProfile + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = fullPath[prefix.Length..];
        var separatorIndex = rest.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return separatorIndex < 0 ? rest : rest[..separatorIndex];
    }

    /// <summary>
    /// <paramref name="fullPath"/> が <paramref name="root"/> 自身、またはその配下かどうか。
    /// </summary>
    private static bool IsUnder(string fullPath, string root)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
