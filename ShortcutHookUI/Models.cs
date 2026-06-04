using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShortcutHookUI;

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class BindingEntry
{
    public string trigger { get; set; } = "";
    public string output  { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? app    { get; set; }  // null = global; process name (e.g. "Code.exe") for app-scoped
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? enabled  { get; set; }  // null/true = active; false = disabled (preserved but not loaded)
}

public sealed class ConfigRoot
{
    public bool altHScroll { get; set; } = false;
    public List<BindingEntry> bindings { get; set; } = new();
}

public sealed class MouseGestureDef
{
    public string Gesture { get; }
    public string Label { get; }
    public MouseGestureDef(string g, string l) { Gesture = g; Label = l; }
}

public sealed class ParsedKey
{
    public int Mods;
    public int[] Keys = System.Array.Empty<int>();
}
