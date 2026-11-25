using CtrlAltDaze.Core.Catalog;

namespace CtrlAltDaze.Core.Model;

public enum SequenceEditRefusal
{
    None,
    Empty,
    Taken,
    Missing,

    /// <summary>A flow's <c>android.input</c> step plays it: removing it would break that step.</summary>
    InUse,

    /// <summary>A key that isn't a key name (<see cref="AndroidKeys.IsName"/>).</summary>
    BadKey,
}

/// <summary>
/// Library › Android › Sequences (D-078, D-156): add, rename (the steps that play it follow), remove (refused while
/// used) and set the keys of an Android device's sequences. Pure, so it is tested here.
/// </summary>
public static class AndroidSequenceEdits
{
    /// <summary>The step and its params keep their ids (D-104 choice 9: no id renames); the step's title is "Play sequence".</summary>
    public const string SequenceStep = "android.input", DeviceParam = "device", SequenceParam = "input";

    /// <summary>"sequence", then "sequence-2", "sequence-3"… whichever is free on this device.</summary>
    public static string NewName(AndroidDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        const string stem = "sequence";
        if (!device.Sequences.ContainsKey(stem))
            return stem;
        for (var n = 2; ; n++)
        {
            if (!device.Sequences.ContainsKey($"{stem}-{n}"))
                return $"{stem}-{n}";
        }
    }

    /// <summary>
    /// What a sequence saved before D-156 as D-pad moves ("inputs") sends: wake, open the TV's input list, the moves,
    /// choose. The keys the old step pressed around the moves, so a converted file behaves as before.
    /// </summary>
    public static ValueList<string> FromMoves(IEnumerable<string> moves) =>
        ["wakeup", "tv_input", .. moves.Select(m => m == "up" ? "dpad_up" : "dpad_down"), "dpad_center"];

    /// <summary>A new sequence with no keys, last in the list.</summary>
    public static (Configuration Config, SequenceEditRefusal Refusal) Add(Configuration config, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (Find(config, deviceKey) is not { } device)
            return (config, SequenceEditRefusal.Missing);
        if (name.Length == 0)
            return (config, SequenceEditRefusal.Empty);
        if (device.Sequences.ContainsKey(name))
            return (config, SequenceEditRefusal.Taken);
        return (With(config, device with { Sequences = new([.. device.Sequences, KeyValuePair.Create(name, new ValueList<string>([]))]) }),
            SequenceEditRefusal.None);
    }

    /// <summary>A new name in the same place; every <c>android.input</c> step that played the old one follows (D-046).</summary>
    public static (Configuration Config, SequenceEditRefusal Refusal) Rename(Configuration config, string deviceKey, string name, string newName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(newName);
        newName = newName.Trim();
        if (Find(config, deviceKey) is not { } device || !device.Sequences.ContainsKey(name))
            return (config, SequenceEditRefusal.Missing);
        if (newName.Length == 0)
            return (config, SequenceEditRefusal.Empty);
        if (newName == name)
            return (config, SequenceEditRefusal.None);
        if (device.Sequences.ContainsKey(newName))
            return (config, SequenceEditRefusal.Taken);

        var renamed = With(config, device with
        {
            Sequences = new(device.Sequences.Select(i => i.Key == name ? KeyValuePair.Create(newName, i.Value) : i)),
        });
        var flows = renamed.Flows.Select(f => Uses(f, deviceKey, name)
            ? f with { Nodes = [.. f.Nodes.Select(n => n is StepNode s && Plays(f, s, deviceKey, name) ? Renamed(s, newName) : n)] }
            : f);
        return (renamed with { Flows = [.. flows] }, SequenceEditRefusal.None);
    }

    /// <summary>Refused while a flow plays it (<see cref="SequenceEditRefusal.InUse"/>): that step would fail.</summary>
    public static (Configuration Config, SequenceEditRefusal Refusal) Remove(Configuration config, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Find(config, deviceKey) is not { } device || !device.Sequences.ContainsKey(name))
            return (config, SequenceEditRefusal.Missing);
        if (config.Flows.Any(f => Uses(f, deviceKey, name)))
            return (config, SequenceEditRefusal.InUse);
        return (With(config, device with { Sequences = new(device.Sequences.Where(i => i.Key != name)) }), SequenceEditRefusal.None);
    }

    /// <summary>The keys, in send order; each a key name (<see cref="AndroidKeys.IsName"/>), stored trimmed and lower case.</summary>
    public static (Configuration Config, SequenceEditRefusal Refusal) SetKeys(Configuration config, string deviceKey, string name, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(keys);
        var list = keys.ToList();
        if (Find(config, deviceKey) is not { } device || !device.Sequences.ContainsKey(name))
            return (config, SequenceEditRefusal.Missing);
        if (!list.All(AndroidKeys.IsName))
            return (config, SequenceEditRefusal.BadKey);
        var clean = new ValueList<string>([.. list.Select(k => k.Trim().ToLowerInvariant())]);
        return (With(config, device with
        {
            Sequences = new(device.Sequences.Select(i => i.Key == name ? KeyValuePair.Create(name, clean) : i)),
        }), SequenceEditRefusal.None);
    }

    /// <summary>The flows with a step that plays this sequence, in file order.</summary>
    public static IReadOnlyList<Flow> Users(Configuration config, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        return [.. config.Flows.Where(f => Uses(f, deviceKey, name))];
    }

    public static bool Uses(Flow flow, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Nodes.OfType<StepNode>().Any(s => Plays(flow, s, deviceKey, name));
    }

    /// <summary>
    /// An <c>android.input</c> step plays this sequence when its sequence is the name and its device is this one: by
    /// key, or by one of the flow's inputs (<c>$tv</c>) whose default is this device (the device it runs with unless a
    /// call passes another).
    /// </summary>
    public static bool Plays(Flow flow, StepNode step, string deviceKey, string name)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(step);
        if (step.Step != SequenceStep
            || !step.With.TryGetValue(SequenceParam, out var sequence) || sequence is not ScalarParam { Text: var text } || text.Trim() != name
            || !step.With.TryGetValue(DeviceParam, out var device))
            return false;
        if (FlowInputs.NameIn(device) is { } own)
            return flow.Inputs.TryGetValue(own, out var flowInput) && flowInput.Kind == LibraryKind.Android && flowInput.Default == deviceKey;
        return device is ScalarParam { Text: var key } && key == deviceKey;
    }

    private static StepNode Renamed(StepNode step, string newName) => step with
    {
        With = new(step.With.Select(e => e.Key == SequenceParam ? KeyValuePair.Create(e.Key, (ParamValue)newName) : e)),
    };

    private static AndroidDevice? Find(Configuration config, string deviceKey) =>
        config.Library.Android.FirstOrDefault(d => d.Key == deviceKey);

    private static Configuration With(Configuration config, AndroidDevice device) => config with
    {
        Library = config.Library with { Android = [.. config.Library.Android.Select(d => d.Key == device.Key ? device : d)] },
    };
}
