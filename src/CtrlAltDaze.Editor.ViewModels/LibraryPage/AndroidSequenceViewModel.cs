using CommunityToolkit.Mvvm.ComponentModel;

namespace CtrlAltDaze.Editor.ViewModels.LibraryPage;

/// <summary>One key of a sequence, in send order; × takes it out.</summary>
public sealed record KeyToken(AndroidSequenceViewModel Sequence, int Index, string Key)
{
    public string Text => Key;

    public string RemoveName => $"Remove key {Index + 1}, {Key}, from {Sequence.Name}";

    public override string ToString() => Text;
}

/// <summary>
/// Library › Android › Sequences (D-078, D-156): a named list of Android keys a "Play sequence" step sends in order,
/// and the flows whose steps play it. Edits go through <see cref="LibraryPageViewModel"/>.
/// </summary>
public sealed partial class AndroidSequenceViewModel : ObservableObject
{
    internal AndroidSequenceViewModel(string deviceKey, string name)
    {
        DeviceKey = deviceKey;
        Name = name;
    }

    public string DeviceKey { get; }

    public string Name { get; }

    public string RenameName => $"Rename sequence {Name}";

    public string RemoveName => $"Remove sequence {Name}";

    public string AddKeyName => $"Add a key to {Name}";

    /// <summary>The key names the add box suggests (the same table as Send keys); any other key name is fine too.</summary>
    public static IReadOnlyList<string> KeyChoices => Core.Catalog.AndroidKeys.Names;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeysText), nameof(HasNoKeys), nameof(AutomationName))]
    public partial IReadOnlyList<KeyToken> Keys { get; internal set; } = [];

    public bool HasNoKeys => Keys.Count == 0;

    public string KeysName => $"Keys of {Name}";

    /// <summary>"tv_input, dpad_down, dpad_center", or a nudge while empty.</summary>
    public string KeysText => Keys.Count == 0 ? "No keys yet" : string.Join(", ", Keys.Select(k => k.Text));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(RemoveTip), nameof(UsedByText), nameof(AutomationName))]
    public partial int UsedBy { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool CanEdit { get; internal set; }

    public bool CanRemove => CanEdit && UsedBy == 0;

    /// <summary>Why Remove is off (UX §5.7 voice); the tooltip sits on a wrapper because a disabled button shows none.</summary>
    public string RemoveTip => UsedBy switch
    {
        0 => "Remove this sequence",
        1 => "Used by 1 flow. Change that step first.",
        _ => $"Used by {UsedBy} flows. Change those steps first.",
    };

    public string UsedByText => UsedBy switch
    {
        0 => "Not used yet",
        1 => "Used by 1 flow",
        _ => $"Used by {UsedBy} flows",
    };

    /// <summary>Why the last rename didn't happen, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    public partial string? RenameError { get; internal set; }

    public bool HasRenameError => RenameError is not null;

    /// <summary>Why the last added key didn't go in, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddKeyError))]
    public partial string? AddKeyError { get; internal set; }

    public bool HasAddKeyError => AddKeyError is not null;

    public string AutomationName => $"{Name}, {KeysText}, {UsedByText}";

    internal void Load(IReadOnlyList<string> keys, int usedBy, bool canEdit)
    {
        if (!Keys.Select(k => k.Key).SequenceEqual(keys))
            Keys = [.. keys.Select((k, i) => new KeyToken(this, i, k))];
        UsedBy = usedBy;
        CanEdit = canEdit;
    }
}
