using System.Windows;
using ClaudeBackupApp.ViewModels;

namespace ClaudeBackupApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
