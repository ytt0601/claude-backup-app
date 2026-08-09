using System.Collections.ObjectModel;
using System.Windows;
using ClaudeBackupApp.Models;
using ClaudeBackupApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClaudeBackupApp.ViewModels;

public partial class RestoreViewModel : ObservableObject
{
    private readonly RestoreService _restoreService = new();
    private readonly ClaudeProcessService _processService = new();
    private CancellationTokenSource? _cts;
    private BackupManifest? _manifest;

    public ObservableCollection<CategoryOptionViewModel> Categories { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    private string? backupFilePath;

    [ObservableProperty]
    private string manifestSummary = "バックアップファイルを選択してください。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectBackupFileCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRestoreCommand))]
    private bool isManifestLoaded;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private double progressMaximum = 1;

    [ObservableProperty]
    private string statusText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanSelectBackupFile))]
    private async Task SelectBackupFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "リストアするバックアップファイルを選択",
            Filter = "Claudeバックアップ (*.zip)|*.zip|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            BackupFilePath = dialog.FileName;
            _manifest = await _restoreService.ReadManifestAsync(BackupFilePath, CancellationToken.None);

            Categories.Clear();
            foreach (var category in _manifest.Categories)
            {
                Categories.Add(new CategoryOptionViewModel(
                    category.Id,
                    category.DisplayName,
                    isChecked: category.DefaultEnabled ?? !category.ContainsSensitiveData,
                    category.ContainsSensitiveData));
            }

            ManifestSummary =
                $"作成日時: {_manifest.CreatedAt.LocalDateTime:yyyy/MM/dd HH:mm:ss}\n" +
                $"作成元PC: {_manifest.MachineName} ({_manifest.UserName})\n" +
                $"含まれるカテゴリ: {string.Join(", ", _manifest.Categories.Select(c => c.DisplayName))}";
            IsManifestLoaded = Categories.Count > 0;
        }
        catch (Exception ex)
        {
            IsManifestLoaded = false;
            _manifest = null;
            Categories.Clear();
            ManifestSummary = $"読み込みエラー: {ex.Message}";
            MessageBox.Show(Application.Current.MainWindow, ManifestSummary, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanSelectBackupFile() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunRestore))]
    private async Task RunRestoreAsync()
    {
        if (_manifest is null || BackupFilePath is null)
        {
            return;
        }

        var selectedIds = Categories.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        if (selectedIds.Count == 0)
        {
            MessageBox.Show(Application.Current.MainWindow, "リストア対象を1つ以上選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        while (_processService.IsRunning())
        {
            var result = MessageBox.Show(
                Application.Current.MainWindow,
                "Claude Desktopが起動中です。復元内容が正しく反映されない可能性があります。Claude Desktopを終了してから「OK」を押して再試行してください。\n中断する場合は「キャンセル」を押してください。",
                "Claude起動中",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK)
            {
                return;
            }
        }

        var confirm = MessageBox.Show(
            Application.Current.MainWindow,
            "選択したカテゴリを復元します。上書きされる既存ファイルは復元前に自動的に退避されます。\n実行しますか?",
            "リストアの確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        LogLines.Clear();
        ProgressValue = 0;
        ProgressMaximum = 1;
        _cts = new CancellationTokenSource();

        var progress = new Progress<OperationProgress>(p =>
        {
            ProgressMaximum = Math.Max(p.Total, 1);
            ProgressValue = p.Current;
            if (p.LogEntry is { } entry)
            {
                LogLines.Add($"[{entry.Level}] {entry.Message}");
            }
        });

        try
        {
            StatusText = "リストア中...";
            var evacuationRoot = await _restoreService.RestoreAsync(BackupFilePath, _manifest, selectedIds, progress, _cts.Token);
            StatusText = $"完了(退避先: {evacuationRoot})";
        }
        catch (OperationCanceledException)
        {
            StatusText = "キャンセルしました。";
        }
        catch (Exception ex)
        {
            StatusText = $"エラー: {ex.Message}";
            LogLines.Add($"[Error] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunRestore() => !IsBusy && IsManifestLoaded;

    [RelayCommand]
    private void CancelRestore() => _cts?.Cancel();
}
