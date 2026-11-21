using System.Collections.Generic;

namespace ShortcutHookCore.Models;

public sealed class ProfileEntry
{
    public string name { get; set; } = "";
    public List<BindingEntry> bindings { get; set; } = new();
}
