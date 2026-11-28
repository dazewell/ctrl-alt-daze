using System.Runtime.InteropServices;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Pc;

/// <summary>The keyboard's toggle keys, by virtual-key code.</summary>
public enum LockKey : ushort
{
    CapsLock = 0x14,
    NumLock = 0x90,
    ScrollLock = 0x91,
}

/// <summary>The lock keys' on/off state, behind a seam so tests never change the user's keyboard (D-159).</summary>
public interface ILockKeys
{
    bool IsOn(LockKey key);

    /// <summary>Lets go of Win, Ctrl, Alt and Shift if they are still down (the shortcut that ran the flow).</summary>
    void ReleaseModifiers();

    /// <summary>Presses and releases the key once: Windows flips its state.</summary>
    void Press(LockKey key);

    /// <summary>Presses and releases Shift: turns Caps Lock off when Windows is set to "Press Shift to turn off Caps Lock".</summary>
    void PressShift();
}

/// <summary>
/// The lock keys' names in YAML and in sentences. Shared by <c>pc.setLockKey</c> and <c>pc.lockKey</c>; the catalog's
/// choices are these keys.
/// </summary>
public static class LockKeys
{
    public static readonly IReadOnlyDictionary<string, (LockKey Key, string Name)> Choices = new Dictionary<string, (LockKey, string)>(StringComparer.Ordinal)
    {
        ["numLock"] = (LockKey.NumLock, "Num Lock"),
        ["capsLock"] = (LockKey.CapsLock, "Caps Lock"),
        ["scrollLock"] = (LockKey.ScrollLock, "Scroll Lock"),
    };

    public static (LockKey Key, string Name) From(StepContext context)
    {
        var choice = context.RequiredText("key");
        return Choices.TryGetValue(choice, out var key)
            ? key
            : throw new StepFailedException($"\"{choice}\" isn't a lock key ({string.Join(", ", Choices.Keys)}).");
    }

    public static string OnOff(bool on) => on ? "on" : "off";
}

/// <summary>
/// <c>pc.setLockKey</c> "Set lock key" (D-159): turns Num Lock, Caps Lock or Scroll Lock on, off, or the other way. It reads
/// the state first and presses the key only when it must, so On twice stays on. No app needs to be in front: the lock
/// state is the whole PC's.
/// </summary>
public sealed class PcSetLockKeyProvider(ILockKeys keys, TimeProvider time) : IStepProvider
{
    /// <summary>How long Windows gets to show the new state after the press.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    public string Id => "pc.setLockKey";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var (key, name) = LockKeys.From(context);
        var state = context.RequiredText("state");
        var was = keys.IsOn(key);
        var want = state switch
        {
            "on" => true,
            "off" => false,
            "toggle" => !was,
            _ => throw new StepFailedException($"\"{state}\" isn't a lock key state (on, off, toggle)."),
        };
        if (was == want)
        {
            context.SetSummary($"{name} was already {LockKeys.OnOff(want)}");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

        keys.ReleaseModifiers();
        keys.Press(key);
        var reached = await ReachedAsync(key, want, cancellationToken).ConfigureAwait(false);
        if (!reached && key == LockKey.CapsLock && !want)
        {
            // "Press Shift to turn off Caps Lock" (Settings > Time & language > Typing > Advanced): the key itself only turns it on.
            keys.PressShift();
            reached = await ReachedAsync(key, want, cancellationToken).ConfigureAwait(false);
        }
        if (!reached)
            throw new StepFailedException($"Windows didn't turn {name} {LockKeys.OnOff(want)}. The PC may be locked, or an app running as administrator is in front.");
        context.SetSummary($"{name} is {LockKeys.OnOff(want)}");
    }

    private async Task<bool> ReachedAsync(LockKey key, bool want, CancellationToken cancellationToken)
    {
        var start = time.GetTimestamp();
        while (true)
        {
            if (keys.IsOn(key) == want)
                return true;
            if (time.GetElapsedTime(start) >= Settle)
                return false;
            await Task.Delay(Poll, time, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary><c>pc.lockKey</c>: the chosen lock key is on.</summary>
public sealed class LockKeyProbe(ILockKeys keys) : IFactProbe
{
    public string Fact => "pc.lockKey";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var (key, name) = LockKeys.From(context);
        var on = keys.IsOn(key);
        return Task.FromResult(new FactReading(on, $"{name} is {LockKeys.OnOff(on)}"));
    }
}

/// <summary>
/// GetKeyState's toggle bit (current for any thread, checked on 10.0.26300) and SendInput presses like Press keys'. Nothing
/// is held between calls (D-009).
/// </summary>
public sealed partial class WindowsLockKeys : ILockKeys
{
    private const ushort Shift = 0xA0;

    private readonly WindowsKeyboard _keyboard = new();

    public bool IsOn(LockKey key) => (GetKeyState((int)key) & 1) != 0;

    public void ReleaseModifiers() => _keyboard.ReleaseModifiers();

    public void Press(LockKey key) => WindowsKeyboard.Tap((ushort)key);

    public void PressShift() => WindowsKeyboard.Tap(Shift);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int key);
}
