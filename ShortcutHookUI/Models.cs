namespace ShortcutHookUI;

public enum ActionKind
{
    Shortcut,
    OpenApp,
    OpenFile,
    OpenFolder,
    Command,
    TypeText,
    ShiftHome,
    ShiftEnd,
    CtrlShiftLeft,
    CtrlShiftRight,
    HScrollLeft,
    HScrollRight,
    TogglePause,
    SwitchProfile
}

public record ActionDef(string Label, ActionKind Kind);

public sealed class MouseGestureDef
{
    public string     Gesture        { get; }
    public string     Label          { get; }
    public ActionDef? GestureDefault { get; }
    
    public MouseGestureDef(string g, string l, ActionDef? gestureDefault = null)
    { 
        Gesture = g; 
        Label = l; 
        GestureDefault = gestureDefault; 
    }
}
