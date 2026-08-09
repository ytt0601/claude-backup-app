using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ClaudeBackupApp.Models;
using ClaudeBackupApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClaudeBackupApp.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly BackupService _backupService = new();
    private readonly ClaudeProcessService _processService = new();
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CategoryOptionViewModel> Categories { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    private string destinationFolder;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private double progressMaximum = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBackupCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = string.Empty;

    public BackupViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        var settings = _settingsService.Load();
        destinationFolder = settings.DefaultBackupFolder ?? SettingsService.DefaultBackupFolder;

        Categories = new ObservableCollection<CategoryOptionViewModel>(
            PathCatalog.Categories.Select(c =>
                new CategoryOptionViewModel(c.Id, c.DisplayName, c.DefaultEnabled, c.ContainsSensitiveData)));
    }

    [RelayCommand]
    private void ChangeDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "バックアップの保存先を選択",
            InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : SettingsService.DefaultBackupFolder,
        };

        if (dialog.ShowDialog() == true)
        {
            DestinationFolder = dialog.FolderName;
            _settingsService.Save(new AppSettings { DefaultBackupFolder = DestinationFolder });
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBackup))]
    private async Task RunBackupAsync()
    {
        if (_processService.IsRunning())
        {
            var result = MessageBox.Show(
                "Claude Desktopが起動中です。データの整合性のため、終了してから実行することを推奨します。\nこのまま続行しますか?",
                "Claude起動中",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var selectedIds = Categories.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        if (selectedIds.Count == 0)
        {
            MessageBox.Show("バックアップ対象を1つ以上選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
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
            StatusText = "バックアップ中...";
            var zipPath = await _backupService.CreateBackupAsync(selectedIds, DestinationFolder, progress, _cts.Token);
            StatusText = $"完了: {zipPath}";
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

    private bool CanRunBackup() => !IsBusy;

    [RelayCommand]
    private void CancelBackup() => _cts?.Cancel();
}
