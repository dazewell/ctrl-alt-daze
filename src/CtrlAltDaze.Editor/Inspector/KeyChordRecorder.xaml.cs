using CtrlAltDaze.Core.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace CtrlAltDaze.Editor.Inspector;

/// <summary>
/// Records one chord for Press keys (D-157), started by a click, Enter or Space (focus alone never records, as for
/// <see cref="HotkeyRecorder"/>). Unlike a shortcut, any key counts, Esc, Tab and Enter included, so recording takes
/// exactly one chord and stops; leaving the box cancels. It stores nothing: it raises <see cref="Recorded"/>.
/// </summary>
public sealed partial class KeyChordRecorder : UserControl
{
    private const string Prompt = "Press the keys";

    private bool _recording;
    private HotkeyModifiers _seen; // modifiers that went down since all keys were last up, with no other key yet
    private DeactivationWatch? _watch;

    public KeyChordRecorder()
    {
        InitializeComponent();
        Box.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Start()), true);
    }

    /// <summary>A chord was pressed, in its canonical text ("Ctrl+Shift+M").</summary>
    public event EventHandler<string>? Recorded;

    /// <summary>True while recording: registered shortcuts must not fire then (D-021).</summary>
    public event EventHandler<bool>? RecordingChanged;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                e.Handled = true;
                Start();
            }
            return;
        }
        e.Handled = true;
        var modifiers = HeldModifiers();
        if (IsModifier(e.Key))
        {
            _seen |= modifiers;
            Box.Text = Hotkey.Prefix(modifiers);
            return;
        }
        _seen = HotkeyModifiers.None;
        if (KeyChord.TryCreate(modifiers, (uint)e.Key, out var chord))
            Add(chord);
        else
        {
            ShowNote("That key can't be pressed by a flow. Type it in the box above if it has a name.");
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
            return;
        e.Handled = true;
        if (HeldModifiers() != HotkeyModifiers.None)
            return;
        var seen = _seen;
        _seen = HotkeyModifiers.None;
        Box.Text = Prompt;
        // One of Ctrl, Alt or Shift pressed and let go alone is that key. Win alone, or several modifiers, most likely lost
        // their key to Windows or another app's shortcut (Win+D, a Ctrl+Alt+X of another tool): say so instead of guessing.
        if (seen is HotkeyModifiers.Ctrl or HotkeyModifiers.Alt or HotkeyModifiers.Shift
            && KeyChord.TryCreate(HotkeyModifiers.None, KeyChord.ModifierKeys.Single(m => m.Flag == seen).Key, out var alone))
            Add(alone);
        else if ((seen & ~HotkeyModifiers.Shift) != HotkeyModifiers.None)
            ShowNote("Windows or another app kept that shortcut, so it can't be recorded. Type it in the box above.");
    }

    private void Add(KeyChord chord)
    {
        Stop();
        ShowNote(null);
        Recorded?.Invoke(this, chord.ToString());
        Announce("Added " + chord);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => Stop();

    private void Start()
    {
        if (_recording)
            return;
        _recording = true;
        _seen = HotkeyModifiers.None;
        _watch = DeactivationWatch.Start(Stop);
        ShowNote(null);
        RecordingChanged?.Invoke(this, true);
        Box.Focus(FocusState.Pointer);
        Box.Text = Prompt;
    }

    private void Stop()
    {
        var was = _recording;
        _recording = false;
        _seen = HotkeyModifiers.None;
        _watch?.Dispose();
        _watch = null;
        Box.Text = "";
        if (was)
            RecordingChanged?.Invoke(this, false);
    }

    private void ShowNote(string? text)
    {
        Note.Text = text ?? "";
        Note.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null)
            Announce(text);
    }

    private void Announce(string text) =>
        (FrameworkElementAutomationPeer.FromElement(Box) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Box))?.RaiseNotificationEvent(
            AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, "KeyChordRecorderNote");

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl
        or VirtualKey.RightControl or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static HotkeyModifiers HeldModifiers()
    {
        static bool Down(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var modifiers = HotkeyModifiers.None;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
            modifiers |= HotkeyModifiers.Win;
        if (Down(VirtualKey.Control))
            modifiers |= HotkeyModifiers.Ctrl;
        if (Down(VirtualKey.Menu))
            modifiers |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift))
            modifiers |= HotkeyModifiers.Shift;
        return modifiers;
    }
}
