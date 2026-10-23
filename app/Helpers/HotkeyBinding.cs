using System;
using System.Windows.Input;

namespace Nitrous.Helpers;

/// <summary>
/// Single source of truth for the "modifiers|virtualKey" hotkey format stored in the registry.
/// </summary>
public static class HotkeyBinding
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static string Create(uint modifiers, uint virtualKey) => $"{modifiers}|{virtualKey}";

    public static bool TryParse(string? value, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split('|');
        if (parts.Length != 2) return false;

        return uint.TryParse(parts[0], out modifiers) &&
               uint.TryParse(parts[1], out virtualKey) &&
               virtualKey > 0;
    }

    public static string Format(string? value)
    {
        if (!TryParse(value, out var modifiers, out var virtualKey)) return "None";

        var label = string.Empty;
        if ((modifiers & ModControl) != 0) label += "Ctrl + ";
        if ((modifiers & ModShift) != 0) label += "Shift + ";
        if ((modifiers & ModAlt) != 0) label += "Alt + ";
        if ((modifiers & ModWin) != 0) label += "Win + ";

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        return key == Key.None ? "None" : label + key;
    }
}
