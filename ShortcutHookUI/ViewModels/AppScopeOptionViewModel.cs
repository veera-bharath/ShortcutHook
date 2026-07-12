using System;

namespace ShortcutHookUI.ViewModels;

public class AppScopeOptionViewModel : ViewModelBase
{
    public string DisplayName { get; }
    public string ProcessName { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    public Action? SelectionChanged { get; set; }

    public AppScopeOptionViewModel(string displayName, string processName, bool isChecked)
    {
        DisplayName = displayName;
        ProcessName = processName;
        _isChecked = isChecked;
    }
}
