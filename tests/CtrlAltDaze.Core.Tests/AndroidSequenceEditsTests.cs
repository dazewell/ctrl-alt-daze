using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

public class AndroidSequenceEditsTests
{
    private static readonly Configuration Config = ConfigYaml.Load("""
        library:
          android:
            tv: { address: 192.168.1.50, sequences: { hdmi1: [], pc: [dpad_down, dpad_down] } }
            den: { address: 192.168.1.51, sequences: { pc: [dpad_up] } }
        flows:
          a:
            nodes:
              - { id: in, step: android.input, with: { device: tv, input: pc } }
              - { id: den, step: android.input, with: { device: den, input: pc }, after: [in] }
          b:
            inputs:
              screen: { kind: android, default: tv }
            nodes:
              - { id: in, step: android.input, with: { device: $screen, input: pc } }
          c:
            nodes:
              - { id: in, step: android.input, with: { device: tv, input: hdmi1 } }
        """).Config!;

    [Fact]
    public void TheFixtureLoads() => Assert.Equal(3, Config.Flows.Count);

    [Fact]
    public void RenameKeepsThePlaceAndTheStepsFollow()
    {
        var (config, refusal) = AndroidSequenceEdits.Rename(Config, "tv", "pc", " desk ");

        Assert.Equal(SequenceEditRefusal.None, refusal);
        var tv = config.Library.Android.First(d => d.Key == "tv");
        Assert.Equal(["hdmi1", "desk"], tv.Sequences.Keys);
        Assert.Equal(["dpad_down", "dpad_down"], tv.Sequences["desk"]);
        Assert.Equal("desk", Input(config, "a", "in"));
        Assert.Equal("pc", Input(config, "a", "den")); // the other device's input of the same name stays
        Assert.Equal("desk", Input(config, "b", "in")); // through the flow's input whose default is tv
        Assert.Equal("hdmi1", Input(config, "c", "in"));
        Assert.Equal(Config.Flows[2], config.Flows[2]);
    }

    [Theory]
    [InlineData("tv", "pc", "  ", SequenceEditRefusal.Empty)]
    [InlineData("tv", "pc", "hdmi1", SequenceEditRefusal.Taken)]
    [InlineData("tv", "nope", "x", SequenceEditRefusal.Missing)]
    [InlineData("nope", "pc", "x", SequenceEditRefusal.Missing)]
    [InlineData("tv", "pc", "pc", SequenceEditRefusal.None)]
    public void RenameRefusesWithoutChanging(string device, string name, string newName, SequenceEditRefusal expected)
    {
        var (config, refusal) = AndroidSequenceEdits.Rename(Config, device, name, newName);

        Assert.Equal(expected, refusal);
        Assert.Same(Config, config);
    }

    [Fact]
    public void AddPutsANewSequenceWithNoKeysLast()
    {
        var device = Config.Library.Android[0];
        var name = AndroidSequenceEdits.NewName(device);
        var (config, refusal) = AndroidSequenceEdits.Add(Config, "tv", name);

        Assert.Equal("sequence", name);
        Assert.Equal(SequenceEditRefusal.None, refusal);
        var tv = config.Library.Android[0];
        Assert.Equal(["hdmi1", "pc", "sequence"], tv.Sequences.Keys);
        Assert.Empty(tv.Sequences["sequence"]);
        Assert.Equal("sequence-2", AndroidSequenceEdits.NewName(tv));
        Assert.Equal(SequenceEditRefusal.Taken, AndroidSequenceEdits.Add(config, "tv", "sequence").Refusal);
        Assert.Equal(SequenceEditRefusal.Empty, AndroidSequenceEdits.Add(config, "tv", " ").Refusal);
        Assert.Equal(SequenceEditRefusal.Missing, AndroidSequenceEdits.Add(config, "nope", "x").Refusal);
    }

    [Fact]
    public void RemoveIsRefusedWhileAFlowPicksIt()
    {
        Assert.Equal(SequenceEditRefusal.InUse, AndroidSequenceEdits.Remove(Config, "tv", "pc").Refusal);
        Assert.Equal(SequenceEditRefusal.InUse, AndroidSequenceEdits.Remove(Config, "tv", "hdmi1").Refusal);

        var unused = Config with { Flows = [Config.Flows[0]] }; // only a's steps: tv/pc, den/pc
        var (config, refusal) = AndroidSequenceEdits.Remove(unused, "tv", "hdmi1");

        Assert.Equal(SequenceEditRefusal.None, refusal);
        Assert.Equal(["pc"], config.Library.Android[0].Sequences.Keys);
    }

    [Fact]
    public void UsersCountTheFlowsThatPickIt()
    {
        Assert.Equal(["a", "b"], AndroidSequenceEdits.Users(Config, "tv", "pc").Select(f => f.Id));
        Assert.Equal(["a"], AndroidSequenceEdits.Users(Config, "den", "pc").Select(f => f.Id));
        Assert.Equal(["c"], AndroidSequenceEdits.Users(Config, "tv", "hdmi1").Select(f => f.Id));
    }

    [Fact]
    public void SetKeysKeepsTheOrderAndTakesOnlyKeyNames()
    {
        var (config, refusal) = AndroidSequenceEdits.SetKeys(Config, "tv", "hdmi1", ["tv_input", " DPAD_DOWN ", "KEYCODE_BUTTON_A"]);

        Assert.Equal(SequenceEditRefusal.None, refusal);
        Assert.Equal(["tv_input", "dpad_down", "keycode_button_a"], config.Library.Android[0].Sequences["hdmi1"]);
        Assert.Equal(["hdmi1", "pc"], config.Library.Android[0].Sequences.Keys);
        Assert.Equal(SequenceEditRefusal.BadKey, AndroidSequenceEdits.SetKeys(Config, "tv", "pc", ["left arrow"]).Refusal);
        Assert.Equal(SequenceEditRefusal.BadKey, AndroidSequenceEdits.SetKeys(Config, "tv", "pc", [""]).Refusal);
        Assert.Equal(SequenceEditRefusal.Missing, AndroidSequenceEdits.SetKeys(Config, "tv", "nope", []).Refusal);
    }

    [Fact]
    public void OldMovesBecomeTheKeysTheOldStepPressed() =>
        Assert.Equal(["wakeup", "tv_input", "dpad_up", "dpad_down", "dpad_center"], AndroidSequenceEdits.FromMoves(["up", "down"]));

    [Fact]
    public void NewNamesCountUpFromSequence()
    {
        var device = new AndroidDevice("x", "1.2.3.4", new([KeyValuePair.Create("sequence", new ValueList<string>([]))]));
        Assert.Equal("sequence-2", AndroidSequenceEdits.NewName(device));
        Assert.Equal("sequence", AndroidSequenceEdits.NewName(device with { Sequences = new([]) }));
    }

    [Fact]
    public void EditedSequencesRoundTripThroughYaml()
    {
        var config = AndroidSequenceEdits.Add(Config, "tv", "game").Config;
        config = AndroidSequenceEdits.SetKeys(config, "tv", "game", ["home"]).Config;

        var yaml = ConfigYaml.Save(config);
        var loaded = ConfigYaml.Load(yaml).Config!;

        Assert.Contains("sequences:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs:", yaml.Split("flows:")[0], StringComparison.Ordinal);

        Assert.Equal(["hdmi1", "pc", "game"], loaded.Library.Android[0].Sequences.Keys);
        Assert.Empty(loaded.Library.Android[0].Sequences["hdmi1"]);
        Assert.Equal(["home"], loaded.Library.Android[0].Sequences["game"]);
    }

    private static string Input(Configuration config, string flow, string node) =>
        Assert.IsType<ScalarParam>(((StepNode)config.Flows.First(f => f.Id == flow).FindNode(node)!).With["input"]).Text;
}
