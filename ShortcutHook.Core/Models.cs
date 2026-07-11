using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShortcutHookCore;

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class BindingEntry
{
    public string trigger { get; set; } = "";
    
    // Legacy single-output field — read-only for backward compat with old JSON.
    // Normalized to outputs on load; never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? output  { get; set; }
    
    // Chain of outputs. Always written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? outputs { get; set; }
    
    // Delay between chained actions (ms). Omitted when 0.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int outputDelay { get; set; } = 0;
    
    // Legacy single-app field — read-only for backward compat with old JSON.
    // Normalized to apps on load; never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? app    { get; set; }
    
    // Multi-app scope. Null/empty = global (fires everywhere). Always written when scoped.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? apps { get; set; }
    
    // Global binding suppressed in these apps. Null/empty = no exclusions.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? exceptApps { get; set; }
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? enabled  { get; set; }
    
    // Debounce: ignore repeated scroll firings within 200 ms. Omitted when false.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool debounce { get; set; } = false;
    
    // Show a brief on-screen toast when this binding fires. Omitted when false.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool showToast { get; set; } = false;
    
    // Optional user-defined note/label for this binding row. UI-only metadata.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? label { get; set; }
}

public sealed class ConfigRoot
{
    public bool altHScroll { get; set; } = false;
    public string activeProfile { get; set; } = "Default";
    public List<ProfileEntry> profiles { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ignoredApps { get; set; }

    // Legacy top-level bindings from pre-profile configs. Read-only — used to detect
    // and migrate old-format files. Never written back.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BindingEntry>? bindings { get; set; }
}

public sealed class ProfileEntry
{
    public string name { get; set; } = "";
    public List<BindingEntry> bindings { get; set; } = new();
}


public sealed class ParsedKey
{
    public int Mods;
    public int[] Keys = System.Array.Empty<int>();
}
