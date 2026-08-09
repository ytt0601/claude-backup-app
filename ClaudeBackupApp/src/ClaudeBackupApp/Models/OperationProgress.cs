namespace ClaudeBackupApp.Models;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

public record OperationLogEntry(LogLevel Level, string Message);

public class OperationProgress
{
    public int Current { get; init; }
    public int Total { get; init; }
    public string? CurrentItem { get; init; }
    public OperationLogEntry? LogEntry { get; init; }
}
