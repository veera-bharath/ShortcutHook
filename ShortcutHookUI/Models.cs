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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? enabled  { get; set; }
}

public sealed class ConfigRoot
{
    public bool altHScroll { get; set; } = false;
    public List<BindingEntry> bindings { get; set; } = new();
}

public record ActionDef(string Label, ActionKind Kind);

public sealed class MouseGestureDef
{
    public string     Gesture        { get; }
    public string     Label          { get; }
    public ActionDef? GestureDefault { get; }
    public MouseGestureDef(string g, string l, ActionDef? gestureDefault = null)
    { Gesture = g; Label = l; GestureDefault = gestureDefault; }
}

public sealed class ParsedKey
{
    public int Mods;
    public int[] Keys = System.Array.Empty<int>();
}
