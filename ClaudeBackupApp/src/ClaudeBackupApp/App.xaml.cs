using System.Windows;
using System.Windows.Threading;

namespace ClaudeBackupApp;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// UIスレッドで発生した未処理例外の最後の砦。個々のコマンドでの想定外の失敗(ディスク容量不足、
    /// 権限エラー等)でアプリ全体が無言でクラッシュしないよう、エラーダイアログを表示して継続する。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            Current.MainWindow,
            $"予期しないエラーが発生しました:\n{e.Exception.Message}",
            "エラー",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
