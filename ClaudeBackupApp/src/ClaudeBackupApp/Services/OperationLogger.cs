using System.IO;
using ClaudeBackupApp.Models;

namespace ClaudeBackupApp.Services;

/// <summary>
/// バックアップ/リストア実行中の進捗をIProgressへ通知しつつ、
/// 同じ内容を %APPDATA%\ClaudeBackupApp\Logs\ 配下のログファイルへも保存する(要件5.3)。
/// BackupService/RestoreServiceで重複していた進捗報告ロジックの共通化も兼ねる。
/// </summary>
public sealed class OperationLogger : IDisposable
{
    private readonly StreamWriter? _writer;

    public string? LogFilePath { get; }

    public OperationLogger(string operationName)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeBackupApp", "Logs");
            Directory.CreateDirectory(logDir);

            LogFilePath = Path.Combine(logDir, $"{operationName}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");
            _writer = new StreamWriter(LogFilePath, append: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログファイルを作成できなくても、画面表示(IProgress)は継続する
            _writer = null;
            LogFilePath = null;
        }
    }

    public void Report(IProgress<OperationProgress>? progress, int current, int total, string currentItem, LogLevel level, string message)
    {
        // messageにはmanifest.json由来の文字列(復元先パス・カテゴリ表示名)が埋め込まれる。
        // manifestは信用できないため、改行を含む値をそのまま書くと「1行1エントリ」のログに
        // 偽の完了行を捏造できてしまう。UI(ListBox)の表示崩れも防ぐため通知側にも適用する。
        message = UntrustedText.Escape(message);

        progress?.Report(new OperationProgress
        {
            Current = current,
            Total = total,
            CurrentItem = currentItem,
            LogEntry = new OperationLogEntry(level, message)
        });

        try
        {
            _writer?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログファイルへの書き込み失敗は処理継続を優先し無視する
        }
    }

    public void Dispose()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログファイルのクローズ失敗は、バックアップ/リストア自体の成否に影響させない
        }
    }
}
