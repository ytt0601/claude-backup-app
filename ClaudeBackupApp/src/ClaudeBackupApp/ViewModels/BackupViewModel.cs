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
    [NotifyCanExecuteChangedFor(nameof(ChangeDestinationCommand))]
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

    [RelayCommand(CanExecute = nameof(CanChangeDestination))]
    private void ChangeDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "バックアップの保存先を選択",
            InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : SettingsService.DefaultBackupFolder,
        };

        if (dialog.ShowDialog() == true)
        {
            // バックアップZIPは暗号化されないため、ネットワーク上の保存先を選ぶと認証情報が
            // そのまま外部へ出ていく。ユーザー自身の明示的な選択は尊重するが、警告は必ず出す。
            if (!SettingsService.IsLocalFixedPath(dialog.FolderName))
            {
                var proceed = MessageBox.Show(
                    Application.Current.MainWindow,
                    $"選択した保存先はローカルドライブではない可能性があります:\n{dialog.FolderName}\n" +
                    "\n" +
                    "バックアップファイルは暗号化されず、認証情報やAPIキーを平文で含みます。" +
                    "ネットワーク共有や同期フォルダに保存すると、これらが外部へ複製されます。\n" +
                    "\n" +
                    "この保存先を使用しますか?",
                    "保存先の確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            DestinationFolder = dialog.FolderName;

            try
            {
                _settingsService.Save(new AppSettings { DefaultBackupFolder = DestinationFolder });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    Application.Current.MainWindow,
                    $"保存先の設定を保存できませんでした: {ex.Message}",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private bool CanChangeDestination() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunBackup))]
    private async Task RunBackupAsync()
    {
        var selectedIds = Categories.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        if (selectedIds.Count == 0)
        {
            MessageBox.Show(Application.Current.MainWindow, "バックアップ対象を1つ以上選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        while (_processService.IsRunning())
        {
            var result = MessageBox.Show(
                Application.Current.MainWindow,
                "Claude Desktopが起動中です。データの整合性のため、Claude Desktopを終了してから「OK」を押して再試行してください。\n中断する場合は「キャンセル」を押してください。",
                "Claude起動中",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK)
            {
                return;
            }
        }

        // 認証情報カテゴリを外しても、Desktop設定・Code設定にはMCPサーバー定義の環境変数などの形で
        // APIキーが入りうる。ZIPは暗号化されないため、取り扱いの注意を実行前に必ず示す(要件6章)。
        var sensitiveNames = Categories
            .Where(c => c.IsChecked && c.ContainsSensitiveData)
            .Select(c => c.DisplayName)
            .ToList();

        if (sensitiveNames.Count > 0)
        {
            var confirm = MessageBox.Show(
                Application.Current.MainWindow,
                "次のカテゴリには認証情報・APIキーが含まれる可能性があります:\n" +
                string.Join("\n", sensitiveNames.Select(n => $"  ・{n}")) + "\n" +
                "\n" +
                "作成されるバックアップファイルは暗号化されません。内容を平文で読み取れるため、" +
                "他人と共有したり、クラウド同期フォルダ・共有フォルダへ置いたりしないでください。\n" +
                "\n" +
                $"保存先: {DestinationFolder}\n" +
                "\n" +
                "バックアップを実行しますか?",
                "バックアップの確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
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
