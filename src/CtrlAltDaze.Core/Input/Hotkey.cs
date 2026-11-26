using System.Diagnostics.CodeAnalysis;

namespace CtrlAltDaze.Core.Input;

/// <summary>Values are <c>RegisterHotKey</c>'s MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>Why a pressed key can't be a shortcut; the recorder shows it under the box (UX §5.7 H1–H4, H7, D-073).</summary>
public enum HotkeyRejection
{
    None,

    /// <summary>H1: not a key a shortcut can use (Space, Tab, arrows, the pad's Enter …).</summary>
    NotAKey,

    /// <summary>H2: a usable key, but Win, Ctrl or Alt is missing.</summary>
    NeedsModifier,

    /// <summary>H3: a number-pad navigation key (Num Lock off). Only the recorder can tell; <see cref="Hotkey.Check"/> never returns it.</summary>
    NumLockOff,

    /// <summary>H4: Shift with a number-pad key; with Num Lock on Windows turns Shift+Num1 into End.</summary>
    ShiftWithNumberPad,

    /// <summary>H7: Win, Ctrl or Alt went down and up with no key arriving, so another app likely took the chord. Recorder only.</summary>
    NothingCameThrough,
}

/// <summary>
/// A global shortcut as written in config.yaml and shown in the editor (ported from OSD D-020..D-022): "Win+Alt+A".
/// <paramref name="Key"/> is a virtual-key code: A–Z, 0–9, F1–F24, the punctuation keys under their US names
/// (<c>[ ] \ ; ' , . / ` - =</c>) or the number pad's keys Num0–Num9 and Num* Num+ Num- Num. Num/ (D-071, D-085). Win, Ctrl or Alt is required, so a
/// hotkey never swallows plain typing, except for the number pad keys, which may stand alone; Shift never goes with a Num key.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, uint Key)
{
    private const uint NumPad0 = 0x60, NumPad9 = 0x69;

    /// <summary>The pad's operator keys (VK_MULTIPLY, ADD, SUBTRACT, DECIMAL, DIVIDE) with their canonical symbol and aliases (D-085).</summary>
    private static readonly (uint Key, char Symbol, string[] Aliases)[] PadOperators =
    [
        (0x6A, '*', ["MULTIPLY", "TIMES", "STAR", "ASTERISK"]),
        (0x6B, '+', ["ADD", "PLUS"]),
        (0x6D, '-', ["SUBTRACT", "MINUS"]),
        (0x6E, '.', ["DECIMAL", "DOT", "PERIOD"]),
        (0x6F, '/', ["DIVIDE", "SLASH"]),
    ];

    internal static readonly (string Name, HotkeyModifiers Flag)[] ModifierNames =
    [
        ("Win", HotkeyModifiers.Win),
        ("Ctrl", HotkeyModifiers.Ctrl),
        ("Alt", HotkeyModifiers.Alt),
        ("Shift", HotkeyModifiers.Shift),
    ];

    /// <summary>VK_OEM_* codes with their US-layout names; YAML keeps these whatever the layout (D-071).</summary>
    private static readonly (char Name, uint Key)[] Punctuation =
    [
        (';', 0xBA), ('=', 0xBB), (',', 0xBC), ('-', 0xBD), ('.', 0xBE), ('/', 0xBF),
        ('`', 0xC0), ('[', 0xDB), ('\\', 0xDC), (']', 0xDD), ('\'', 0xDE),
    ];

    /// <summary>Case and spaces don't matter; "Control", "Windows" and "Numpad1" are accepted too.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (SplitChord(text) is not { } parts)
            return false;
        return ParseModifiers(parts) is { } modifiers && KeyCode(parts[^1]) is { } key && TryCreate(modifiers, key, out hotkey);
    }

    /// <summary>"Win+Alt+A" → ["Win", "Alt", "A"]; a trailing '+' right after "Num" is the pad's plus key. Null when nothing is written.</summary>
    internal static string[]? SplitChord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (!text.EndsWith('+'))
            return text.Split('+', StringSplitOptions.TrimEntries);
        // "Num+" and "Win+Num++": a trailing '+' is the pad's plus key when "Num"/"Numpad" comes right before it.
        var body = text[..^1].TrimEnd();
        if (body.EndsWith('+'))
            body = body[..^1].TrimEnd();
        var parts = body.Split('+', StringSplitOptions.TrimEntries);
        if (parts[^1].ToUpperInvariant() is not ("NUM" or "NUMPAD"))
            return null;
        parts[^1] = "Num+";
        return parts;
    }

    /// <summary>The modifiers before the last part of <paramref name="parts"/>; null when one is unknown or written twice.</summary>
    internal static HotkeyModifiers? ParseModifiers(string[] parts)
    {
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var flag = ModifierFlag(part);
            if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag))
                return null;
            modifiers |= flag;
        }
        return modifiers;
    }

    /// <summary>"Win", "Windows", "Ctrl", "Control", "Alt" or "Shift" in any case; <see cref="HotkeyModifiers.None"/> otherwise.</summary>
    internal static HotkeyModifiers ModifierFlag(string name) => name.ToUpperInvariant() switch
    {
        "WIN" or "WINDOWS" => HotkeyModifiers.Win,
        "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        _ => HotkeyModifiers.None,
    };

    /// <summary>From keys pressed in the editor: false unless <paramref name="key"/> and the modifiers make a valid hotkey.</summary>
    public static bool TryCreate(HotkeyModifiers modifiers, uint key, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        var valid = Check(modifiers, key) == HotkeyRejection.None;
        hotkey = valid ? new Hotkey(modifiers, key) : null;
        return valid;
    }

    /// <summary>VK_NUMPAD0–9 and the pad's operators; the digits and Num. arrive only while Num Lock is on. Num Enter is not here: RegisterHotKey can't tell it from Enter (D-085).</summary>
    public static bool IsNumberPad(uint key) =>
        key is >= NumPad0 and <= NumPad9 || Array.Exists(PadOperators, o => o.Key == key);

    /// <summary>Why <paramref name="key"/> with <paramref name="modifiers"/> can't be a shortcut, or <see cref="HotkeyRejection.None"/>.</summary>
    public static HotkeyRejection Check(HotkeyModifiers modifiers, uint key)
    {
        if (IsNumberPad(key))
            return modifiers.HasFlag(HotkeyModifiers.Shift) ? HotkeyRejection.ShiftWithNumberPad : HotkeyRejection.None;
        var usable = key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x87 // A–Z, 0–9, F1–F24
            || Array.Exists(Punctuation, p => p.Key == key);
        if (!usable)
            return HotkeyRejection.NotAKey;
        return (modifiers & (HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) != 0
            ? HotkeyRejection.None
            : HotkeyRejection.NeedsModifier;
    }

    /// <summary>The held modifiers alone, for the recorder box while keys are going down: "Win+Alt+".</summary>
    public static string Prefix(HotkeyModifiers modifiers) =>
        string.Concat(ModifierNames.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name + "+"));

    /// <summary>A shortcut key's virtual-key code from its name (A, 7, F11, [, Num1, Num+), or null.</summary>
    internal static uint? KeyCode(string name)
    {
        name = name.ToUpperInvariant();
        if (name.Length == 1 && (char.IsAsciiLetterUpper(name[0]) || char.IsAsciiDigit(name[0])))
            return name[0]; // VK_A–VK_Z and VK_0–VK_9 are the ASCII codes
        if (name.Length == 1 && Array.FindIndex(Punctuation, p => p.Name == name[0]) is >= 0 and var i)
            return Punctuation[i].Key;
        if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24
            && name[1] != '0')
            return (uint)(0x70 + n - 1); // VK_F1
        name = name.Replace(" ", "");
        foreach (var prefix in (ReadOnlySpan<string>)["NUMPAD", "NUM"])
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
            {
                var rest = name[prefix.Length..];
                foreach (var (key, symbol, aliases) in PadOperators)
                {
                    if (rest.Length == 1 && rest[0] == symbol || Array.IndexOf(aliases, rest) >= 0)
                        return key;
                }
            }
            if (name.Length == prefix.Length + 1 && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsAsciiDigit(name[^1]))
                return NumPad0 + (uint)(name[^1] - '0');
        }
        return null;
    }

    /// <summary>The key's own name, as written after the last "+": "A", "F11", "[", "Num1".</summary>
    public string KeyName => NameOf(Key);

    /// <summary>The name of a key <see cref="KeyCode"/> reads.</summary>
    internal static string NameOf(uint key) => key switch
    {
        >= 0x70 and <= 0x87 => "F" + (key - 0x70 + 1),
        >= NumPad0 and <= NumPad9 => "Num" + (key - NumPad0),
        _ when Array.FindIndex(PadOperators, o => o.Key == key) is >= 0 and var j => "Num" + PadOperators[j].Symbol,
        _ when Array.FindIndex(Punctuation, p => p.Key == key) is >= 0 and var i => Punctuation[i].Name.ToString(),
        _ => ((char)key).ToString(),
    };

    /// <summary>Canonical form, modifiers in Windows' order: "Win+Ctrl+Alt+Shift+A".</summary>
    public override string ToString() =>
        string.Join('+', ModifierNames.Where(m => Modifiers.HasFlag(m.Flag)).Select(m => m.Name).Append(KeyName));
}
