using System.IO;
using System.Text;
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
        message = SanitizeForLog(message);

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

    /// <summary>
    /// ログ1件が1行に収まるよう、改行・制御文字をエスケープ表記へ置き換える。
    /// 元の文字が何であったかは残す(削除ではなく可視化する)ため、調査時の情報は失わない。
    /// </summary>
    private static string SanitizeForLog(string message)
    {
        var needsEscape = false;
        foreach (var ch in message)
        {
            if (char.IsControl(ch))
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            return message;
        }

        var builder = new StringBuilder(message.Length + 16);
        foreach (var ch in message)
        {
            switch (ch)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }

        return builder.ToString();
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
