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
            "選択したカテゴリを復元します。上書きされる既存ファイルは復元前に自動的に退避されます。\n" +
            "\n" +
            "【ご確認ください】バックアップファイルは、必ずご自身が作成したものか、信頼できる入手元のものだけを使用してください。\n" +
            "Claudeの設定ファイルには外部コマンドを起動する定義が含まれるため、細工されたバックアップを復元すると、" +
            "次回のClaude起動時に意図しないプログラムが実行される可能性があります。\n" +
            "\n" +
            "実行しますか?",
            "リストアの確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (!ConfirmCodeExecutionRisk(selectedIds))
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

        // 退避先と退避件数は処理中のコールバックで受け取る。中断・エラーで終わった場合こそ
        // 「退避された元ファイルがどこにあるか」の案内が要るため、戻り値では間に合わない。
        string? evacuationRoot = null;
        var evacuatedCount = 0;
        void OnEvacuationUpdated(string root, int count)
        {
            evacuationRoot = root;
            evacuatedCount = count;
        }

        try
        {
            StatusText = "リストア中...";
            await _restoreService.RestoreAsync(
                BackupFilePath, _manifest, selectedIds, progress, OnEvacuationUpdated, _cts.Token);
            StatusText = AppendEvacuationHint("リストアが完了しました。", evacuationRoot, evacuatedCount);
        }
        catch (OperationCanceledException)
        {
            StatusText = AppendEvacuationHint("キャンセルしました。", evacuationRoot, evacuatedCount);
        }
        catch (Exception ex)
        {
            StatusText = AppendEvacuationHint($"エラー: {ex.Message}", evacuationRoot, evacuatedCount);
            LogLines.Add($"[Error] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 復元対象に「復元しただけで外部コマンドの起動につながりうるファイル」が含まれる場合、
    /// 該当ファイルを列挙して最終確認を取る。
    /// PathCatalog.IsAllowedRestoreDestination は復元先をClaude関連パスに限定するが、
    /// その許可範囲の中にMCPサーバー定義・hooks・プラグイン/スキルのコードが含まれるため、
    /// パス検証だけでは「信用できないZIPによる任意コード実行」を防げない。
    /// 何が書き換わるのかを具体的に見せることが、最後の防御線になる。
    /// </summary>
    /// <returns>続行してよい場合はtrue</returns>
    private bool ConfirmCodeExecutionRisk(IReadOnlyList<string> selectedCategoryIds)
    {
        if (_manifest is null)
        {
            return false;
        }

        var riskyPaths = _manifest.Categories
            .Where(c => selectedCategoryIds.Contains(c.Id))
            .SelectMany(c => c.Files)
            .Select(f => PathCatalog.ResolvePortablePath(f.RootKind, f.RelativePath) ?? f.Destination)
            .Where(PathCatalog.IsCodeExecutionRisk)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (riskyPaths.Count == 0)
        {
            return true;
        }

        const int maxShown = 10;
        var shown = string.Join("\n", riskyPaths.Take(maxShown).Select(p => $"  ・{p}"));
        if (riskyPaths.Count > maxShown)
        {
            shown += $"\n  ほか{riskyPaths.Count - maxShown}件";
        }

        var result = MessageBox.Show(
            Application.Current.MainWindow,
            "次のファイルは、Claudeが外部プログラムを起動するための定義(MCPサーバー設定・hooks・プラグイン/スキル)を" +
            "含む可能性があります。これらを復元すると、次回のClaude起動時にバックアップ作成者が指定したプログラムが" +
            "実行される場合があります。\n" +
            "\n" +
            $"{shown}\n" +
            "\n" +
            "信頼できるバックアップであることを確認したうえで続行してください。復元を続けますか?",
            "外部プログラムが実行される可能性があります",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// ステータス文に退避先フォルダの案内を付ける。正常終了・中断・エラーのいずれの経路でも、
    /// 退避された元ファイルが実際に残っている場合にのみ付ける。
    /// 判定にフォルダの実在を使わないのは、RestoreServiceが退避に失敗した場合でも
    /// (File.Moveの前に親フォルダを作るため)フォルダだけは存在しうるからで、
    /// 存在しない・空のフォルダを案内すると元ファイルが保全されていると誤解させてしまう。
    /// </summary>
    private static string AppendEvacuationHint(string message, string? evacuationRoot, int evacuatedCount)
    {
        if (evacuatedCount <= 0 || string.IsNullOrEmpty(evacuationRoot))
        {
            return message;
        }

        return $"{message} 退避済みの元ファイル({evacuatedCount}件)は次の場所にあります: {evacuationRoot}";
    }

    private bool CanRunRestore() => !IsBusy && IsManifestLoaded;

    [RelayCommand]
    private void CancelRestore() => _cts?.Cancel();
}
