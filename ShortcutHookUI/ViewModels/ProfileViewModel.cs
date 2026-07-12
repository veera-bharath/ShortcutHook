using System;
using System.Windows.Input;

namespace ShortcutHookUI.ViewModels;

public class ProfileViewModel : ViewModelBase
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isSelectedForExport;
    public bool IsSelectedForExport
    {
        get => _isSelectedForExport;
        set => SetProperty(ref _isSelectedForExport, value);
    }

    public ICommand SelectCommand { get; }
    public ICommand SelectForExportCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }

    public ProfileViewModel(string name, Action<ProfileViewModel> select, Action<ProfileViewModel> selectExport, Action<ProfileViewModel> rename, Action<ProfileViewModel> delete)
    {
        Name = name;
        SelectCommand = new RelayCommand(() => select(this));
        SelectForExportCommand = new RelayCommand(() => selectExport(this));
        RenameCommand = new RelayCommand(() => rename(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }
}
