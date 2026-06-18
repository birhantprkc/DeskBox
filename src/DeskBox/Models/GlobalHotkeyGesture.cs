namespace DeskBox.Models;

[Flags]
public enum HotkeyModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4
}

public readonly record struct GlobalHotkeyGesture(HotkeyModifierKeys Modifiers, int VirtualKey);
