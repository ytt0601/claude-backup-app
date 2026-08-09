using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeBackupApp.ViewModels;

public partial class CategoryOptionViewModel : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }
    public bool ContainsSensitiveData { get; }

    [ObservableProperty]
    private bool isChecked;

    public CategoryOptionViewModel(string id, string displayName, bool isChecked, bool containsSensitiveData)
    {
        Id = id;
        DisplayName = displayName;
        this.isChecked = isChecked;
        ContainsSensitiveData = containsSensitiveData;
    }
}
