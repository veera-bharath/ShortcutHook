using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ShortcutHookCore.Models;
using ShortcutHookUI.Models;
using ShortcutHookUI.Services;

namespace ShortcutHookUI.ViewModels;

public class RowViewModel : ViewModelBase
{
    private bool _isGlobal = true;
    public bool IsGlobal
    {
        get => _isGlobal;
        set
        {
            if (SetProperty(ref _isGlobal, value))
            {
                UpdateAppScopeOptions();
                UpdateAppScopeLabel();
                _markDirty();
            }
        }
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _markDirty();
            }
        }
    }

    private bool _debounce;
    public bool Debounce
    {
        get => _debounce;
        set
        {
            if (SetProperty(ref _debounce, value))
            {
                _markDirty();
            }
        }
    }

    private bool _showToast;
    public bool ShowToast
    {
        get => _showToast;
        set
        {
            if (SetProperty(ref _showToast, value))
            {
                _markDirty();
            }
        }
    }

    private string _noteLabel = "";
    public string NoteLabel
    {
        get => _noteLabel;
        set
        {
            if (SetProperty(ref _noteLabel, value))
            {
                _markDirty();
            }
        }
    }

    private int _outputDelay;
    public int OutputDelay
    {
        get => _outputDelay;
        set
        {
            if (SetProperty(ref _outputDelay, value))
            {
                // Sync to all subsequent steps in the chain (index >= 1)
                for (int i = 1; i < Chain.Count; i++)
                {
                    Chain[i].Delay = value;
                }
                _markDirty();
            }
        }
    }

    public ObservableCollection<ChainedActionViewModel> Chain { get; } = new();

    public List<string> Apps { get; } = new();
    public List<string> ExceptApps { get; } = new();

    private string _appScopeLabel = "Global";
    public string AppScopeLabel
    {
        get => _appScopeLabel;
        set => SetProperty(ref _appScopeLabel, value);
    }

    public ObservableCollection<AppScopeOptionViewModel> AppScopeOptions { get; } = new();

    public bool IsMouseTrigger { get; }
    public bool DebounceVisible { get; }
    public bool HasAppScope { get; }
    public ActionDef[] AvailableActions { get; }

    public ICommand AddActionCommand { get; }
    public ICommand DeleteRowCommand { get; }
    public ICommand ToggleGlobalCommand { get; }

    private readonly Action _markDirty;
    private readonly List<AppEntry> _allApps;
    private readonly ObservableCollection<ProfileViewModel> _profiles;
    private readonly Action<ChainedActionViewModel> _recordAction;

    public RowViewModel(Action markDirty, List<AppEntry> allApps, ObservableCollection<ProfileViewModel> profiles,
                        Action<ChainedActionViewModel> recordAction, Action<RowViewModel> deleteRow,
                        bool isMouseTrigger, string? gesture, bool hasAppScope, ActionDef[] availableActions)
    {
        _markDirty = markDirty;
        _allApps = allApps;
        _profiles = profiles;
        _recordAction = recordAction;
        IsMouseTrigger = isMouseTrigger;
        HasAppScope = hasAppScope;
        AvailableActions = availableActions;

        if (isMouseTrigger && gesture != null)
        {
            DebounceVisible = gesture.Contains("scroll");
        }

        AddActionCommand = new RelayCommand(() =>
        {
            var item = new ChainedActionViewModel(_markDirty, _allApps, _profiles, DeleteActionStep, _recordAction);
            Chain.Add(item);
            UpdateMultiStepFlags();
            _markDirty();
        });

        DeleteRowCommand = new RelayCommand(() => deleteRow(this));

        ToggleGlobalCommand = new RelayCommand(() =>
        {
            IsGlobal = !IsGlobal;
        });
    }

    public void DeleteActionStep(ChainedActionViewModel step)
    {
        if (Chain.Count > 1)
        {
            Chain.Remove(step);
            UpdateMultiStepFlags();
            _markDirty();
        }
    }

    public void UpdateMultiStepFlags()
    {
        bool multi = Chain.Count > 1;
        foreach (var item in Chain)
        {
            item.IsMultiStep = multi;
        }
    }

    public void InitializeChain(List<string> outputs, List<int>? outputDelays, int outputDelay)
    {
        Chain.Clear();
        var effectiveDelays = GetEffectiveDelays(outputs, outputDelays, outputDelay);
        for (int i = 0; i < outputs.Count; i++)
        {
            var item = new ChainedActionViewModel(_markDirty, _allApps, _profiles, DeleteActionStep, _recordAction);
            item.ParseOutputString(outputs[i], AvailableActions);
            if (i < effectiveDelays.Count)
                item.Delay = effectiveDelays[i];
            Chain.Add(item);
        }
        if (Chain.Count == 0)
        {
            Chain.Add(new ChainedActionViewModel(_markDirty, _allApps, _profiles, DeleteActionStep, _recordAction));
        }
        UpdateMultiStepFlags();
    }

    private List<int> GetEffectiveDelays(List<string> outputs, List<int>? outputDelays, int outputDelay)
    {
        var result = new List<int>();
        int count = outputs.Count > 0 ? outputs.Count : 1;
        for (int i = 0; i < count; i++)
        {
            if (outputDelays != null && i < outputDelays.Count)
                result.Add(outputDelays[i]);
            else
                result.Add(i == 0 ? 0 : outputDelay);
        }
        return result;
    }

    public void InitializeAppScope(bool isGlobal, List<string> apps, List<string>? exceptApps)
    {
        _isGlobal = isGlobal;
        Apps.Clear();
        Apps.AddRange(apps);
        ExceptApps.Clear();
        ExceptApps.AddRange(exceptApps ?? new List<string>());

        UpdateAppScopeOptions();
        UpdateAppScopeLabel();
    }

    public void UpdateAppScopeOptions()
    {
        AppScopeOptions.Clear();
        var activeList = IsGlobal ? ExceptApps : Apps;
        var selectedSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);

        var entriesByProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _allApps)
        {
            var procName = AppScanner.ResolveProcessName(app.Path);
            if (string.IsNullOrEmpty(procName)) continue;
            if (!entriesByProc.ContainsKey(procName)) entriesByProc[procName] = app.Name;
        }

        foreach (var stored in activeList)
            if (!entriesByProc.ContainsKey(stored)) entriesByProc[stored] = stored;

        foreach (var pair in entriesByProc.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            var procName = pair.Key;
            var display = pair.Value;
            var option = new AppScopeOptionViewModel(display, procName, selectedSet.Contains(procName));
            option.SelectionChanged = () =>
            {
                var list = IsGlobal ? ExceptApps : Apps;
                if (option.IsChecked)
                {
                    if (!list.Contains(procName, StringComparer.OrdinalIgnoreCase)) list.Add(procName);
                }
                else
                {
                    list.RemoveAll(a => string.Equals(a, procName, StringComparison.OrdinalIgnoreCase));
                }
                UpdateAppScopeLabel();
                _markDirty();
            };
            AppScopeOptions.Add(option);
        }
    }

    public void UpdateAppScopeLabel()
    {
        var list = IsGlobal ? ExceptApps : Apps;
        if (list.Count == 0)
        {
            AppScopeLabel = IsGlobal ? "Global" : "No Apps";
        }
        else
        {
            var prefix = IsGlobal ? "Except: " : "Only: ";
            AppScopeLabel = prefix + string.Join(", ", list);
        }
    }
}
