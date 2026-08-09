using ClaudeBackupApp.Services;

namespace ClaudeBackupApp.ViewModels;

public class MainViewModel
{
    public BackupViewModel Backup { get; }
    public RestoreViewModel Restore { get; }

    public MainViewModel()
    {
        var settingsService = new SettingsService();
        Backup = new BackupViewModel(settingsService);
        Restore = new RestoreViewModel();
    }
}
