namespace ShortcutHookCore.Models;

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public override string ToString() => Name;
}
