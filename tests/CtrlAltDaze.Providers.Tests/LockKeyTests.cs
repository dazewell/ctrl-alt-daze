using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Providers.Pc;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>D-159: <c>pc.setLockKey</c> and <c>pc.lockKey</c> behind fake lock keys; no test changes the real keyboard.</summary>
public class LockKeyTests
{
    private sealed class FakeLockKeys : ILockKeys
    {
        public HashSet<LockKey> On { get; } = [];
        public List<string> Log { get; } = [];

        /// <summary>Windows ignores the press (UIPI, the PC locked).</summary>
        public bool Ignore { get; init; }

        /// <summary>"Press Shift to turn off Caps Lock": the Caps key only turns it on.</summary>
        public bool ShiftTurnsCapsOff { get; init; }

        public bool IsOn(LockKey key) => On.Contains(key);

        public void ReleaseModifiers() => Log.Add("release");

        public void Press(LockKey key)
        {
            Log.Add(key.ToString());
            if (Ignore || ShiftTurnsCapsOff && key == LockKey.CapsLock && On.Contains(key))
                return;
            if (!On.Remove(key))
                On.Add(key);
        }

        public void PressShift()
        {
            Log.Add("Shift");
            if (!Ignore && ShiftTurnsCapsOff)
                On.Remove(LockKey.CapsLock);
        }
    }

    private static async Task<StepContext> Set(FakeLockKeys keys, params (string, string)[] with)
    {
        var context = Steps.Context("pc.setLockKey", with);
        await new PcSetLockKeyProvider(keys, TimeProvider.System).ExecuteAsync(context, TestContext.Current.CancellationToken);
        return context;
    }

    [Theory]
    [InlineData("numLock", LockKey.NumLock)]
    [InlineData("capsLock", LockKey.CapsLock)]
    [InlineData("scrollLock", LockKey.ScrollLock)]
    public async Task OnTurnsAnOffKeyOnAfterLettingGoOfHeldModifiers(string choice, LockKey key)
    {
        var keys = new FakeLockKeys();
        await Set(keys, ("key", choice), ("state", "on"));
        Assert.Contains(key, keys.On);
        Assert.Equal(["release", key.ToString()], keys.Log);
    }

    [Fact]
    public async Task TheDefaultIsNumLockOn()
    {
        var keys = new FakeLockKeys();
        await Set(keys);
        Assert.Equal([LockKey.NumLock], keys.On);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task AKeyAlreadyThereIsNotPressed(string state, bool on)
    {
        var keys = new FakeLockKeys();
        if (on)
            keys.On.Add(LockKey.NumLock);
        await Set(keys, ("key", "numLock"), ("state", state));
        Assert.Empty(keys.Log);
        Assert.Equal(on, keys.On.Contains(LockKey.NumLock));
    }

    [Fact]
    public async Task OffTurnsAnOnKeyOff()
    {
        var keys = new FakeLockKeys();
        keys.On.Add(LockKey.ScrollLock);
        await Set(keys, ("key", "scrollLock"), ("state", "off"));
        Assert.Empty(keys.On);
    }

    [Fact]
    public async Task ToggleFlipsEitherWay()
    {
        var keys = new FakeLockKeys();
        await Set(keys, ("key", "capsLock"), ("state", "toggle"));
        Assert.Contains(LockKey.CapsLock, keys.On);
        await Set(keys, ("key", "capsLock"), ("state", "toggle"));
        Assert.Empty(keys.On);
    }

    [Fact]
    public async Task CapsLockOffUsesShiftWhenWindowsIsSetToTurnItOffWithShift()
    {
        var keys = new FakeLockKeys { ShiftTurnsCapsOff = true };
        keys.On.Add(LockKey.CapsLock);
        await Set(keys, ("key", "capsLock"), ("state", "off"));
        Assert.Empty(keys.On);
        Assert.Equal(["release", "CapsLock", "Shift"], keys.Log);
    }

    [Fact]
    public async Task APressWindowsIgnoresFailsWithASentence()
    {
        var keys = new FakeLockKeys { Ignore = true };
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Set(keys, ("key", "numLock"), ("state", "on")));
        Assert.Equal("Windows didn't turn Num Lock on. The PC may be locked, or an app running as administrator is in front.", e.Message);
    }

    [Theory]
    [InlineData("key", "insert", "isn't a lock key")]
    [InlineData("state", "blink", "isn't a lock key state")]
    public async Task AnUnknownChoiceFailsAndPressesNothing(string param, string value, string message)
    {
        var keys = new FakeLockKeys();
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Set(keys, (param, value)));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Empty(keys.Log);
    }

    [Fact]
    public async Task ACancelledRunPressesNothing()
    {
        var keys = new FakeLockKeys();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PcSetLockKeyProvider(keys, TimeProvider.System).ExecuteAsync(Steps.Context("pc.setLockKey"), cts.Token));
        Assert.Empty(keys.Log);
    }

    [Fact]
    public async Task TheCheckSaysTheStateAndFailsWhenItIsNotWhatWasChosen()
    {
        var keys = new FakeLockKeys();
        keys.On.Add(LockKey.CapsLock);
        var check = new FactCheckProvider("pc.lockKey", new LockKeyProbe(keys), "on");
        var token = TestContext.Current.CancellationToken;

        var context = Steps.Context("pc.lockKey", ("key", "capsLock"), ("is", "on"));
        await check.ExecuteAsync(context, token);
        var e = await Assert.ThrowsAsync<CheckNotMetException>(() => check.ExecuteAsync(Steps.Context("pc.lockKey", ("key", "numLock"), ("is", "on")), token));
        Assert.Equal("Num Lock is off", e.Message);
        await check.ExecuteAsync(Steps.Context("pc.lockKey", ("key", "numLock"), ("is", "off")), token);
    }

    [Fact]
    public void TheCatalogAndTheProvidersAgree()
    {
        foreach (var step in new[] { "pc.setLockKey", "pc.lockKey" })
            Assert.Equal(LockKeys.Choices.Keys.Order(), ProviderCatalog.Find(step)!.FindParam("key")!.Choices.Order());
        Assert.Contains("pc.lockKey", ProviderCatalog.Checks);
        Assert.DoesNotContain("pc.setLockKey", ProviderCatalog.Checks);
    }
}
