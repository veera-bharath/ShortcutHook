using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ShortcutHookCore.Models;
using ShortcutHookUI.Services;

namespace ShortcutHookUI.ViewModels;

public class AppTriggerCardViewModel : ViewModelBase
{
    private string _kind = "launch";
    public string Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                _markDirty();
            }
        }
    }

    public int SelectedKindIndex
    {
        get => Kind == "exit" ? 1 : Kind == "focus" ? 2 : Kind == "blur" ? 3 : 0;
        set
        {
            Kind = value == 1 ? "exit" : value == 2 ? "focus" : value == 3 ? "blur" : "launch";
        }
    }

    public ObservableCollection<string> SelectedApps { get; } = new();

    private string _appsLabel = "No apps selected";
    public string AppsLabel
    {
        get => _appsLabel;
        set => SetProperty(ref _appsLabel, value);
    }

    public ObservableCollection<AppScopeOptionViewModel> AppOptions { get; } = new();

    public RowViewModel Row { get; }

    private string _customAppInput = "";
    public string CustomAppInput
    {
        get => _customAppInput;
        set => SetProperty(ref _customAppInput, value);
    }

    public ICommand DeleteCardCommand { get; }
    public ICommand AddCustomAppCommand { get; }

    private readonly Action _markDirty;
    private readonly List<AppEntry> _allApps;

    public AppTriggerCardViewModel(Action markDirty, List<AppEntry> allApps, ObservableCollection<ProfileViewModel> profiles,
                                   Action<ChainedActionViewModel> recordAction, Action<AppTriggerCardViewModel> deleteCard)
    {
        _markDirty = markDirty;
        _allApps = allApps;

        Row = new RowViewModel(_markDirty, _allApps, profiles, recordAction, _ => {},
                             isMouseTrigger: false, null, hasAppScope: false, MainWindowViewModel.StandardActions);

        DeleteCardCommand = new RelayCommand(() => deleteCard(this));

        AddCustomAppCommand = new RelayCommand(() =>
        {
            var name = CustomAppInput.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

            if (!SelectedApps.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                SelectedApps.Add(name);
                UpdateAppOptionsSelection();
                UpdateAppsLabel();
                _markDirty();
            }
            CustomAppInput = "";
        });
    }

    public void InitializeSelectedApps(List<string> apps)
    {
        SelectedApps.Clear();
        foreach (var app in apps) SelectedApps.Add(app);

        UpdateAppOptionsSelection();
        UpdateAppsLabel();
    }

    public void UpdateAppOptionsSelection()
    {
        AppOptions.Clear();
        var selectedSet = new HashSet<string>(SelectedApps, StringComparer.OrdinalIgnoreCase);

        var entriesByProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _allApps)
        {
            var procName = AppScanner.ResolveProcessName(app.Path);
            if (string.IsNullOrEmpty(procName)) continue;
            if (!entriesByProc.ContainsKey(procName)) entriesByProc[procName] = app.Name;
        }

        foreach (var stored in SelectedApps)
            if (!entriesByProc.ContainsKey(stored)) entriesByProc[stored] = stored;

        foreach (var pair in entriesByProc.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            var procName = pair.Key;
            var display = pair.Value;
            var option = new AppScopeOptionViewModel(display, procName, selectedSet.Contains(procName));
            option.SelectionChanged = () =>
            {
                if (option.IsChecked)
                {
                    if (!SelectedApps.Contains(procName, StringComparer.OrdinalIgnoreCase))
                        SelectedApps.Add(procName);
                }
                else
                {
                    for (int i = SelectedApps.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(SelectedApps[i], procName, StringComparison.OrdinalIgnoreCase))
                            SelectedApps.RemoveAt(i);
                    }
                }
                UpdateAppsLabel();
                _markDirty();
            };
            AppOptions.Add(option);
        }
    }

    public void UpdateAppsLabel()
    {
        if (SelectedApps.Count == 0)
        {
            AppsLabel = "No apps selected";
        }
        else
        {
            AppsLabel = string.Join(", ", SelectedApps);
        }
    }
}
