using CtrlAltDaze.Core.Input;
using CtrlAltDaze.Providers.Pc;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>D-157: <c>pc.keys</c> behind a fake keyboard; no test presses a real key or moves a window.</summary>
public class PcKeysTests
{
    private sealed class FakeKeyboard : IKeyboard
    {
        public KeyTarget? InFront { get; set; } = new(1, 100, "notepad", false);
        public Dictionary<string, (KeyTarget? Window, bool Running)> Programs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AllowFront { get; init; } = true;
        public List<string> Log { get; } = [];

        public KeyTarget? Front() => InFront;

        public KeyTarget? MainWindow(string program, out bool running)
        {
            var found = Programs.TryGetValue(program, out var p) ? p : (null, false);
            running = found.Running;
            return found.Window;
        }

        public bool BringToFront(KeyTarget target)
        {
            Log.Add("front " + target.Program);
            if (AllowFront)
                InFront = target;
            return AllowFront;
        }

        public void ReleaseModifiers() => Log.Add("release");

        public void Press(KeyChord chord) => Log.Add(chord.ToString());
    }

    private static Task Run(FakeKeyboard keyboard, string? program, params string[] keys) =>
        new PcKeysProvider(keyboard, TimeProvider.System).ExecuteAsync(
            Steps.Context("pc.keys", program is null ? [] : [("program", program)]).WithList("keys", keys), TestContext.Current.CancellationToken);

    [Fact]
    public async Task PressesEachChordInOrderToTheAppInFrontAfterLettingGoOfHeldModifiers()
    {
        var keyboard = new FakeKeyboard();
        await Run(keyboard, null, "ctrl+shift+m", "F11", "enter");
        Assert.Equal(["release", "Ctrl+Shift+M", "F11", "Enter"], keyboard.Log);
    }

    [Fact]
    public async Task ANamedProgramComesToTheFrontFirst()
    {
        var keyboard = new FakeKeyboard();
        keyboard.Programs["Teams"] = (new KeyTarget(7, 200, "Teams", false), true);
        await Run(keyboard, @"C:\Apps\Teams.exe", "Ctrl+Shift+M");
        Assert.Equal(["front Teams", "release", "Ctrl+Shift+M"], keyboard.Log);
    }

    [Fact]
    public async Task ANamedProgramAlreadyInFrontIsLeftAlone()
    {
        var keyboard = new FakeKeyboard { InFront = new KeyTarget(9, 201, "Teams", false) };
        keyboard.Programs["Teams"] = (new KeyTarget(7, 200, "Teams", false), true);
        await Run(keyboard, "teams", "Space");
        Assert.Equal(["release", "Space"], keyboard.Log);
    }

    [Theory]
    [InlineData(false, "Teams isn't running.")]
    [InlineData(true, "Teams has no window to press keys in.")]
    public async Task AProgramWithoutAWindowFailsWithASentence(bool running, string message)
    {
        var keyboard = new FakeKeyboard();
        keyboard.Programs["Teams"] = (null, running);
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(keyboard, "Teams", "Enter"));
        Assert.Equal(message, e.Message);
        Assert.Empty(keyboard.Log);
    }

    [Fact]
    public async Task AProgramWindowsKeepsBehindFailsAndPressesNothing()
    {
        var keyboard = new FakeKeyboard { AllowFront = false };
        keyboard.Programs["Teams"] = (new KeyTarget(7, 200, "Teams", false), true);
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(keyboard, "Teams", "Enter"));
        Assert.Contains("didn't let Teams come to the front", e.Message, StringComparison.Ordinal);
        Assert.Equal(["front Teams"], keyboard.Log);
    }

    [Fact]
    public async Task AnElevatedAppFailsBeforeAnyKey()
    {
        var keyboard = new FakeKeyboard { InFront = new KeyTarget(1, 100, "regedit", true) };
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(keyboard, null, "F5"));
        Assert.Contains("regedit is running as administrator", e.Message, StringComparison.Ordinal);
        Assert.Empty(keyboard.Log);

        keyboard.Programs["taskmgr"] = (new KeyTarget(3, 300, "Taskmgr", true), true);
        e = await Assert.ThrowsAsync<StepFailedException>(() => Run(keyboard, "taskmgr", "F5"));
        Assert.Contains("administrator", e.Message, StringComparison.Ordinal);
        Assert.Empty(keyboard.Log);
    }

    [Fact]
    public async Task NothingInFrontFails()
    {
        var keyboard = new FakeKeyboard { InFront = null };
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(keyboard, null, "Enter"));
        Assert.Contains("locked", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableKeyFailsBeforeAnyKeyIsPressed()
    {
        var keyboard = new FakeKeyboard();
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(keyboard, null, "Enter", "Ctrl+Hyper"));
        Assert.Contains("\"Ctrl+Hyper\" isn't a key to press", e.Message, StringComparison.Ordinal);
        Assert.Empty(keyboard.Log);
    }

    [Fact]
    public async Task NoKeysFails()
    {
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Run(new FakeKeyboard(), null));
        Assert.Equal("Say which keys to press.", e.Message);
    }

    [Fact]
    public async Task AStopBetweenChordsPressesNoMore()
    {
        var keyboard = new FakeKeyboard();
        using var time = new ManualTime();
        using var cts = new CancellationTokenSource();
        var run = new PcKeysProvider(keyboard, time).ExecuteAsync(Steps.Context("pc.keys").WithList("keys", "A", "B"), cts.Token);
        await time.WaitForTimerAsync();
        Assert.Equal(["release", "A"], keyboard.Log);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["release", "A"], keyboard.Log);
    }
}
