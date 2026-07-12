namespace ShortcutHookUI.Models;

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
