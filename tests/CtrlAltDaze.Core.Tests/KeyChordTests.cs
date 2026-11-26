using CtrlAltDaze.Core.Input;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>D-157: the keys Press keys reads, and the validator's rule for them.</summary>
public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+M", "Ctrl+Shift+M")]
    [InlineData("shift + ctrl + m", "Ctrl+Shift+M")]
    [InlineData("F11", "F11")]
    [InlineData("enter", "Enter")]
    [InlineData("Return", "Enter")]
    [InlineData("Escape", "Esc")]
    [InlineData("Alt+Tab", "Alt+Tab")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("Win+P", "Win+P")]
    [InlineData("Windows+Shift+S", "Win+Shift+S")]
    [InlineData("Win", "Win")]
    [InlineData("Ctrl+Alt", "Ctrl+Alt")]
    [InlineData("Page Down", "PageDown")]
    [InlineData("pgup", "PageUp")]
    [InlineData("Ctrl+Num+", "Ctrl+Num+")]
    [InlineData("Num5", "Num5")]
    [InlineData("Ctrl+[", "Ctrl+[")]
    [InlineData("Del", "Delete")]
    [InlineData("PrtSc", "PrintScreen")]
    public void ReadsAChordAndWritesItCanonically(string text, string canonical)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(canonical, chord.ToString());
        Assert.True(KeyChord.TryParse(chord.ToString(), out var again));
        Assert.Equal(chord, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+Ctrl")]
    [InlineData("Hyper+A")]
    [InlineData("F25")]
    [InlineData("AB")]
    [InlineData("Ctrl+A+B")]
    [InlineData("+")]
    public void RefusesWhatIsNotAKey(string? text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void EveryExampleIsAKey()
    {
        foreach (var example in KeyChord.Examples)
        {
            Assert.True(KeyChord.TryParse(example, out var chord), example);
            Assert.Equal(example, chord.ToString());
        }
    }

    [Fact]
    public void AChordThatCouldBeAShortcutMatchesIt()
    {
        Assert.True(KeyChord.TryParse("Alt+Win+K", out var chord));
        Assert.Equal("Win+Alt+K", chord.AsHotkey()!.ToString());
        Assert.True(KeyChord.TryParse("Enter", out var enter));
        Assert.Null(enter.AsHotkey());
    }

    private static Model.ValueList<Problem> Validate(string keys, string hotkeys = "[]")
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    hotkeys: " + hotkeys + "\n    nodes:\n      - { id: k, step: pc.keys, with: { keys: " + keys + " } }\n"
            + "  g:\n    hotkeys: [Ctrl+Alt+G]\n    nodes:\n      - { id: w, step: wait, with: { duration: 1s } }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Fact]
    public void WrittenKeysPass() => Assert.Empty(Validate("[Ctrl+Shift+M, F11, Enter]"));

    [Fact]
    public void AKeyItCantReadIsAnError()
    {
        var problem = Assert.Single(Validate("[Enter, 'Ctrl+Hyper']"));
        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.f.nodes.k.with.keys", problem.Path);
        Assert.Contains("Ctrl+Hyper", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherFlowsShortcutIsAWarning()
    {
        var problem = Assert.Single(Validate("[Alt+Ctrl+G]"));
        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Contains("flow \"g\"", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItsOwnShortcutIsAWarning()
    {
        var problem = Assert.Single(Validate("[Win+F9]", "[Win+F9]"));
        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Contains("own shortcut", problem.Message, StringComparison.Ordinal);
    }
}
