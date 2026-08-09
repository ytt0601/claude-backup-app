using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeBackupApp.Services;

public class ClaudeProcessService
{
    private const string ProcessName = "claude";

    /// <summary>
    /// Claude Code CLI(ターミナルの claude コマンドや、Desktop内蔵の claude-code バイナリ)の
    /// 実行ファイルはFileVersionInfo上の製品名が"Claude Code"。Claude Desktop本体(Electron製)は
    /// 同じファイル名 "claude.exe" だが製品名が異なるため、これで区別する。
    /// </summary>
    private const string CliProductName = "Claude Code";

    /// <summary>
    /// Claude Desktop(Electron製、複数プロセスがすべて "claude.exe" として起動する)が
    /// 起動中かどうかを確認する。同名の Claude Code CLI プロセスは除外する。
    /// </summary>
    public int GetRunningProcessCount()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Count(IsClaudeDesktop);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public bool IsRunning() => GetRunningProcessCount() > 0;

    private static bool IsClaudeDesktop(Process process)
    {
        try
        {
            // "Claude Code" だと積極的に判定できたものだけを除外し、それ以外
            // (将来Desktop側の製品名表記が変わった場合を含む)は安全側に倒してDesktopとして扱う。
            return !string.Equals(process.MainModule?.FileVersionInfo.ProductName, CliProductName, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // 権限不足等でモジュール情報を取得できない場合は、誤って見逃すより
            // 「起動中かもしれない」側に倒し警告を出す(安全側)。
            return true;
        }
    }
}
