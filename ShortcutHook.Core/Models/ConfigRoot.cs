using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShortcutHookCore.Models;

public sealed class ConfigRoot
{
    public bool altHScroll { get; set; } = false;
    public string activeProfile { get; set; } = "Default";
    public List<ProfileEntry> profiles { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ignoredApps { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BindingEntry>? bindings { get; set; }
}
