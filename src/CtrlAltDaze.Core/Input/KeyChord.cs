using System.Diagnostics.CodeAnalysis;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Input;

/// <summary>
/// One key with its modifiers for Press keys (<c>pc.keys</c>, D-157): "Ctrl+Shift+M", "F11", "Enter", "Alt+Tab", "Win".
/// It reads every key a <see cref="Hotkey"/> names (A–Z, 0–9, F1–F24, US punctuation, the number pad) plus the named keys
/// below, and needs no Win, Ctrl or Alt: it is pressed, never registered. A modifier written alone ("Win") is pressed as a key.
/// Case and spaces don't matter.
/// </summary>
public sealed record KeyChord(HotkeyModifiers Modifiers, uint Key)
{
    private const uint LeftWin = 0x5B, LeftShift = 0xA0, LeftCtrl = 0xA2, LeftAlt = 0xA4;

    /// <summary>Virtual-key codes a shortcut can't use, with their canonical name and the other names accepted.</summary>
    private static readonly (string Name, uint Key, string[] Aliases)[] Named =
    [
        ("Enter", 0x0D, ["RETURN"]), ("Esc", 0x1B, ["ESCAPE"]), ("Tab", 0x09, []), ("Space", 0x20, ["SPACEBAR"]),
        ("Backspace", 0x08, ["BACK", "BKSP"]), ("Delete", 0x2E, ["DEL"]), ("Insert", 0x2D, ["INS"]),
        ("Home", 0x24, []), ("End", 0x23, []), ("PageUp", 0x21, ["PGUP"]), ("PageDown", 0x22, ["PGDN"]),
        ("Up", 0x26, ["UPARROW"]), ("Down", 0x28, ["DOWNARROW"]), ("Left", 0x25, ["LEFTARROW"]), ("Right", 0x27, ["RIGHTARROW"]),
        ("PrintScreen", 0x2C, ["PRTSC", "PRTSCN", "PRINT"]), ("Pause", 0x13, ["BREAK"]), ("Menu", 0x5D, ["APPS", "CONTEXTMENU"]),
        ("CapsLock", 0x14, ["CAPS"]), ("NumLock", 0x90, []), ("ScrollLock", 0x91, []),
        ("Win", LeftWin, ["WINDOWS"]), ("Ctrl", LeftCtrl, ["CONTROL"]), ("Alt", LeftAlt, []), ("Shift", LeftShift, []),
    ];

    /// <summary>Common chords for the inspector's list (any other key can be typed).</summary>
    public static ValueList<string> Examples { get; } =
    [
        "Enter", "Esc", "Tab", "Space", "F11", "Alt+Tab", "Alt+F4", "Win+D", "Win+P", "Win+Shift+S", "Ctrl+C", "Ctrl+V",
        "Ctrl+Z", "Ctrl+S", "Ctrl+Shift+M", "Ctrl+Shift+Esc", "Up", "Down", "Left", "Right", "Home", "End", "PageUp",
        "PageDown", "Delete", "Backspace", "PrintScreen", "Menu",
    ];

    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyChord? chord)
    {
        chord = null;
        if (Hotkey.SplitChord(text) is not { } parts || Hotkey.ParseModifiers(parts) is not { } modifiers)
            return false;
        if (KeyCode(parts[^1]) is not { } key)
            return false;
        // "Ctrl+Ctrl" or "Win+Win": the modifier is already held, so the chord means nothing more.
        if (ModifierOf(key) is var self && self != HotkeyModifiers.None && modifiers.HasFlag(self))
            return false;
        chord = new KeyChord(modifiers, key);
        return true;
    }

    /// <summary>The <see cref="Hotkey"/> this chord would fire if a flow has it as its shortcut; null when it can't be one.</summary>
    public Hotkey? AsHotkey() => Hotkey.TryCreate(Modifiers, Key, out var hotkey) ? hotkey : null;

    /// <summary>The modifier a key is (left Win, Ctrl, Alt or Shift pressed alone), else <see cref="HotkeyModifiers.None"/>.</summary>
    public static HotkeyModifiers ModifierOf(uint key) => key switch
    {
        LeftWin => HotkeyModifiers.Win,
        LeftCtrl => HotkeyModifiers.Ctrl,
        LeftAlt => HotkeyModifiers.Alt,
        LeftShift => HotkeyModifiers.Shift,
        _ => HotkeyModifiers.None,
    };

    /// <summary>The virtual-key code of each modifier, in the order they go down: Ctrl, Alt, Shift, Win.</summary>
    public static IReadOnlyList<(HotkeyModifiers Flag, uint Key)> ModifierKeys { get; } =
        [(HotkeyModifiers.Ctrl, LeftCtrl), (HotkeyModifiers.Alt, LeftAlt), (HotkeyModifiers.Shift, LeftShift), (HotkeyModifiers.Win, LeftWin)];

    private static uint? KeyCode(string name)
    {
        if (Hotkey.KeyCode(name) is { } key)
            return key;
        var upper = name.Replace(" ", "").ToUpperInvariant();
        foreach (var (canonical, code, aliases) in Named)
        {
            if (string.Equals(canonical, upper, StringComparison.OrdinalIgnoreCase) || Array.IndexOf(aliases, upper) >= 0)
                return code;
        }
        return null;
    }

    /// <summary>The key's own name, as written after the last "+".</summary>
    public string KeyName => Array.FindIndex(Named, n => n.Key == Key) is >= 0 and var i ? Named[i].Name : Hotkey.NameOf(Key);

    /// <summary>Canonical form, modifiers in Windows' order: "Win+Ctrl+Alt+Shift+A".</summary>
    public override string ToString() =>
        string.Join('+', Hotkey.ModifierNames.Where(m => Modifiers.HasFlag(m.Flag)).Select(m => m.Name).Append(KeyName));
}
