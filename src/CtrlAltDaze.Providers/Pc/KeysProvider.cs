using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CtrlAltDaze.Core.Input;
using CtrlAltDaze.Providers.Process;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Pc;

/// <summary>
/// A top-level window keys can go to. <paramref name="Program"/> is null when its process can't be read. <paramref name="IsFront"/>
/// is false for the app just behind our own window or the taskbar (a run from the tray menu or the editor): it must be brought forward.
/// <paramref name="Hung"/>: Windows marks it as not responding.
/// </summary>
public sealed record KeyTarget(nint Window, int ProcessId, string? Program, bool Elevated, bool IsFront = true, bool Hung = false);

/// <summary>The PC's keyboard and windows, behind a seam so tests never type into the user's apps (D-157).</summary>
public interface IKeyboard
{
    /// <summary>
    /// The window in front, or null when none takes keys (locked, the sign-in screen). When Ctrl Alt Daze's own window or the
    /// taskbar is in front, the first app window under it instead, with <see cref="KeyTarget.IsFront"/> false.
    /// </summary>
    KeyTarget? Front();

    /// <summary>The main window of a running program with this name (no folder, no ".exe"); <paramref name="running"/> says whether any instance runs.</summary>
    KeyTarget? MainWindow(string program, out bool running);

    /// <summary>Restores <paramref name="target"/> if minimised and brings it to the front; true when it is in front afterwards.</summary>
    bool BringToFront(KeyTarget target, CancellationToken cancellationToken);

    /// <summary>Lets go of Win, Ctrl, Alt and Shift if they are still down (the shortcut that ran the flow), so they don't join the chords.</summary>
    void ReleaseModifiers();

    /// <summary>Presses and releases one chord: modifiers down, the key down and up, modifiers up.</summary>
    void Press(KeyChord chord);
}

/// <summary>
/// <c>pc.keys</c> "Press keys" (D-157): each chord in order, to the app in front or to a named program brought to the
/// front first. Keys pressed by software can't reach an app running as administrator (Windows' UIPI), so that fails with
/// a sentence before anything is pressed.
/// </summary>
public sealed class PcKeysProvider(IKeyboard keyboard, TimeProvider time) : IStepProvider
{
    /// <summary>Between chords, so an app that opens something on one key is ready for the next.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(50);

    public string Id => "pc.keys";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var texts = context.TextList("keys");
        if (texts.Count == 0)
            throw new StepFailedException("Say which keys to press.");
        var chords = new List<KeyChord>(texts.Count);
        foreach (var text in texts)
        {
            if (!KeyChord.TryParse(text, out var chord))
                throw new StepFailedException($"\"{text}\" isn't a key to press. Write it like Ctrl+Shift+M, F11 or Enter.");
            chords.Add(chord);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var program = context.Text("program");
        var target = string.IsNullOrWhiteSpace(program) ? InFront(cancellationToken) : Bring(program, cancellationToken);
        if (target.Elevated)
            throw new StepFailedException($"{target.Program ?? "The app in front"} is running as administrator, so it ignores keys pressed by Ctrl Alt Daze.");

        keyboard.ReleaseModifiers();
        for (var i = 0; i < chords.Count; i++)
        {
            if (i > 0)
                await Task.Delay(Gap, time, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            keyboard.Press(chords[i]);
        }
    }

    private KeyTarget InFront(CancellationToken cancellationToken)
    {
        var target = keyboard.Front() ?? throw new StepFailedException("No app is in front to take the keys (the PC may be locked).");
        return target.IsFront ? target : Forward(target, target.Program ?? "The app behind", cancellationToken);
    }

    private KeyTarget Bring(string program, CancellationToken cancellationToken)
    {
        var name = ProcessRunningProbe.ProgramName(Environment.ExpandEnvironmentVariables(program));
        var window = keyboard.MainWindow(name, out var running)
            ?? throw new StepFailedException(running ? $"{name} has no window to press keys in." : $"{name} isn't running.");
        if (window.Elevated)
            throw new StepFailedException($"{name} is running as administrator, so it ignores keys pressed by Ctrl Alt Daze.");
        if (keyboard.Front() is { IsFront: true } front && (front.ProcessId == window.ProcessId || string.Equals(front.Program, name, StringComparison.OrdinalIgnoreCase)))
            return front;
        return Forward(window, name, cancellationToken);
    }

    /// <summary>Brings a window that isn't in front forward; a hung one isn't touched (restoring it would block, D-157).</summary>
    private KeyTarget Forward(KeyTarget target, string name, CancellationToken cancellationToken)
    {
        if (target.Elevated)
            throw new StepFailedException($"{name} is running as administrator, so it ignores keys pressed by Ctrl Alt Daze.");
        if (target.Hung)
            throw new StepFailedException($"{name} isn't responding, so no keys were pressed.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!keyboard.BringToFront(target, cancellationToken))
            throw new StepFailedException($"Windows didn't let {name} come to the front, so no keys were pressed.");
        return target;
    }
}

/// <summary>
/// SendInput keyboard events with each key's scan code (games read those). Nothing is resident: every call reads the
/// state it needs and returns (D-009). Win+L and Ctrl+Alt+Delete can't be pressed by any app; Windows ignores them.
/// </summary>
public sealed partial class WindowsKeyboard : IKeyboard
{
    private const uint InputKeyboard = 1, KeyUp = 0x0002, ExtendedKey = 0x0001;
    private const int ShowRestore = 9;
    private const uint QueryLimitedInformation = 0x1000, TokenQuery = 0x0008, TokenElevationClass = 20;

    /// <summary>Unassigned virtual key: pressed between a modifier's down and up so Windows doesn't treat it as a lone press (Start menu, menu bar).</summary>
    private const ushort MaskKey = 0xE8;

    private const ushort Alt = 0xA4;

    /// <summary>Left and right Win, Ctrl, Alt and Shift.</summary>
    private static readonly ushort[] Modifiers = [0x5B, 0x5C, 0xA2, 0xA3, 0xA4, 0xA5, 0xA0, 0xA1];

    /// <summary>The editor's process name: a flow run with its Run button finds the editor in front.</summary>
    private const string EditorProcess = "CtrlAltDaze.Editor";

    private const int GwHwndNext = 2, GwOwner = 4, GwlExStyle = -20, DwmCloaked = 14;
    private const long ToolWindow = 0x80;

    public KeyTarget? Front()
    {
        var window = GetForegroundWindow();
        if (window == 0)
            return null;
        var front = Describe(window);
        if (!IsOurs(front))
            return front;
        // The tray menu leaves the engine's hidden window in front, the Run button the editor: the keys are meant for the
        // app the user was in, the first app window in z-order that isn't ours.
        for (var next = GetTopWindow(0); next != 0; next = GetWindow(next, GwHwndNext))
        {
            if (IsAppWindow(next) && Describe(next) is var candidate && !IsOurs(candidate))
                return candidate with { IsFront = false };
        }
        return null;
    }

    private static bool IsOurs(KeyTarget target) =>
        target.ProcessId == Environment.ProcessId || string.Equals(target.Program, EditorProcess, StringComparison.OrdinalIgnoreCase)
        || ClassName(target.Window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>A window on the taskbar: visible, not minimised, not owned, not a tool window, not cloaked (another desktop, a suspended app).</summary>
    private static bool IsAppWindow(nint window) =>
        IsWindowVisible(window) && !IsIconic(window) && GetWindow(window, GwOwner) == 0
        && (GetWindowLongPtrW(window, GwlExStyle) & ToolWindow) == 0
        && !(DwmGetWindowAttribute(window, DwmCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0);

    private static string ClassName(nint window)
    {
        var buffer = new char[64];
        var length = GetClassNameW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    public KeyTarget? MainWindow(string program, out bool running)
    {
        running = false;
        var self = Environment.ProcessId;
        KeyTarget? found = null;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(program))
        {
            using (process)
            {
                if (process.Id == self)
                    continue;
                running = true;
                if (found is not null)
                    continue;
                try
                {
                    var window = process.MainWindowHandle;
                    if (window != 0)
                        found = Describe(window);
                }
                catch (InvalidOperationException)
                {
                    // exited meanwhile
                }
            }
        }
        return found;
    }

    public bool BringToFront(KeyTarget target, CancellationToken cancellationToken)
    {
        // Async: ShowWindow on another thread's window waits for that thread, so a hung app would hold the step past its timeout.
        if (IsIconic(target.Window))
            ShowWindowAsync(target.Window, ShowRestore);
        if (!SetForegroundWindow(target.Window) || !IsInFront(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Windows lets a background app take the front only right after a key press: an Alt press, masked so it
            // doesn't open the menu bar, is that press (the documented exception to the foreground lock).
            Send([Key(Alt, down: true), Key(MaskKey, down: true), Key(MaskKey, down: false), Key(Alt, down: false)]);
            SetForegroundWindow(target.Window);
        }
        return IsInFront(target);
    }

    public void ReleaseModifiers()
    {
        var held = Modifiers.Where(k => (GetAsyncKeyState(k) & 0x8000) != 0).ToList();
        if (held.Count == 0)
            return;
        var inputs = new List<Input> { Key(MaskKey, down: true), Key(MaskKey, down: false) };
        inputs.AddRange(held.Select(k => Key(k, down: false)));
        Send([.. inputs]);
    }

    public void Press(KeyChord chord)
    {
        var modifiers = KeyChord.ModifierKeys.Where(m => chord.Modifiers.HasFlag(m.Flag)).Select(m => (ushort)m.Key).ToList();
        var inputs = new List<Input>();
        inputs.AddRange(modifiers.Select(k => Key(k, down: true)));
        inputs.Add(Key((ushort)chord.Key, down: true));
        inputs.Add(Key((ushort)chord.Key, down: false));
        // Win or Alt pressed with only Shift or Ctrl: the key itself already masks them, nothing else to add.
        inputs.AddRange(Enumerable.Reverse(modifiers).Select(k => Key(k, down: false)));
        Send([.. inputs]);
    }

    private static bool IsInFront(KeyTarget target)
    {
        var front = GetForegroundWindow();
        if (front == target.Window)
            return true;
        _ = GetWindowThreadProcessId(front, out var pid);
        return front != 0 && (int)pid == target.ProcessId;
    }

    private static KeyTarget Describe(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var pid);
        string? name = null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // exited meanwhile
        }
        return new KeyTarget(window, (int)pid, name, IsElevated(pid), Hung: IsHungAppWindow(window));
    }

    /// <summary>True when the process runs elevated, or is one whose token we may not even read (then keys can't reach it either).</summary>
    private static bool IsElevated(uint pid)
    {
        var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process == 0)
            return true;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
                return true;
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(uint), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static readonly uint[] Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5];

    private static Input Key(ushort key, bool down)
    {
        var flags = (down ? 0 : KeyUp) | (Array.IndexOf(Extended, (uint)key) >= 0 ? ExtendedKey : 0);
        return new Input { Type = InputKeyboard, Key = key, Scan = (ushort)MapVirtualKeyW(key, 0), Flags = flags };
    }

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Unsafe.SizeOf<Input>());
        if (sent != inputs.Length)
            throw new StepFailedException($"Windows took {sent} of {inputs.Length} key events (error {Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>INPUT with its KEYBDINPUT member; 40 bytes on 64-bit Windows, the size of the union's largest member (MOUSEINPUT).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsHungAppWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetTopWindow(nint parent);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(nint window, [Out] char[] buffer, int length);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, uint informationClass, out uint information, int length, out int returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
