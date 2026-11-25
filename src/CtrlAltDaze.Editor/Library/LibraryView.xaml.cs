using CtrlAltDaze.Editor.ViewModels.Inspector;
using CtrlAltDaze.Editor.ViewModels.LibraryPage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CtrlAltDaze.Editor.Library;

/// <summary>The Library page (UX §5.1); all logic is in <see cref="LibraryPageViewModel"/>.</summary>
public sealed partial class LibraryView : UserControl
{
    private LibraryPageViewModel? _viewModel;

    public LibraryView()
    {
        InitializeComponent();
    }

    public LibraryPageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = value;
            SectionsPivot.ItemsSource = value?.Sections;
            if (value is not null)
            {
                SectionsPivot.SelectedIndex = value.SelectedIndex;
                value.PropertyChanged += OnViewModelChanged;
            }
        }
    }

    /// <summary>The inspector's "Add inputs in the Library" link picks the Android section (L1).</summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPageViewModel.SelectedIndex) && _viewModel is not null
            && SectionsPivot.SelectedIndex != _viewModel.SelectedIndex)
            SectionsPivot.SelectedIndex = _viewModel.SelectedIndex;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && SectionsPivot.SelectedIndex >= 0)
            _viewModel.SelectedIndex = SectionsPivot.SelectedIndex;
    }

    private void OnDeviceRowLoaded(object sender, RoutedEventArgs e) => Inspector.InspectorPane.DisableRow(sender);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnDeviceRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e) =>
        Inspector.InspectorPane.DisableRow(sender);

    private void OnAddClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DeviceOption option)
            _viewModel?.AddCommand.Execute(option);
        // The list sits in the button's flyout: close it once something was added.
        if (sender is FrameworkElement element)
        {
            foreach (var popup in VisualTreeHelperPopups(element))
                popup.IsOpen = false;
        }
    }

    private static IEnumerable<Popup> VisualTreeHelperPopups(FrameworkElement element) =>
        element.XamlRoot is { } root ? Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root) : [];

    private void OnCapture(object sender, RoutedEventArgs e) => _ = _viewModel?.CaptureCommand.ExecuteAsync(null);

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = _viewModel?.RefreshAsync();

    private void OnBindSave(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which rebuilds the list
        if (_viewModel.Bind())
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private void OnAddByHand(object sender, RoutedEventArgs e) => AddByHand(sender as FrameworkElement);

    private void OnAddByHandKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AddByHand(sender as FrameworkElement);
    }

    private void AddByHand(FrameworkElement? element)
    {
        if (element?.Tag is not LibrarySectionViewModel { Kind: { } kind } || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which rebuilds the list
        if (_viewModel.AddByHand(kind))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private void OnUsedByOpen(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            FlyoutBase.ShowAttachedFlyout(element);
    }

    private void OnUsedByClick(object sender, ItemClickEventArgs e)
    {
        CloseFlyouts(sender);
        if (e.ClickedItem is FlowLink flow)
            _viewModel?.OpenFlowCommand.Execute(flow);
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryItemViewModel item)
            _ = _viewModel?.OpenLocationCommand.ExecuteAsync(item);
    }

    private void OnRenameSave(object sender, RoutedEventArgs e) => Rename(sender as FrameworkElement);

    private void OnRenameKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        Rename(sender as FrameworkElement);
    }

    // The Save button and the box share a panel in the item's rename flyout; the item rides on their Tag.
    private void Rename(FrameworkElement? element)
    {
        if (element?.Tag is not LibraryItemViewModel item || element.Parent is not Panel panel
            || panel.Children.OfType<TextBox>().FirstOrDefault() is not { } box || _viewModel is null)
            return;
        if (_viewModel.Rename(item, box.Text))
            CloseFlyouts(element);
    }

    // ---- Android › Edit (D-156): address and adb path ----

    private void OnEditAndroidSave(object sender, RoutedEventArgs e) => EditAndroid(sender as FrameworkElement);

    private void OnEditAndroidKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        EditAndroid(sender as FrameworkElement);
    }

    // The two boxes and the Save button share a panel in the flyout: address first, then adb.exe.
    private void EditAndroid(FrameworkElement? element)
    {
        if (element?.Tag is not LibraryItemViewModel item || element.Parent is not Panel panel || _viewModel is null)
            return;
        var boxes = panel.Children.OfType<TextBox>().ToList();
        if (boxes.Count < 2)
            return;
        var popups = VisualTreeHelperPopups(element).ToList();
        if (_viewModel.EditAndroid(item, boxes[0].Text, boxes[1].Text))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    // ---- Android › Sequences (D-078, D-156) ----

    private void OnRenameSequenceSave(object sender, RoutedEventArgs e) => RenameSequence(sender as FrameworkElement);

    private void OnRenameSequenceKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        RenameSequence(sender as FrameworkElement);
    }

    // Like Rename: the button and the box share a panel in the sequence's flyout; the sequence rides on their Tag.
    private void RenameSequence(FrameworkElement? element)
    {
        if (element?.Tag is not AndroidSequenceViewModel input || BoxBeside(element) is not { } box || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which can rebuild the row
        if (_viewModel.RenameSequence(input, box.Text))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private void OnAddSequenceSave(object sender, RoutedEventArgs e) => AddSequence(sender as FrameworkElement);

    private void OnAddSequenceKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AddSequence(sender as FrameworkElement);
    }

    private void AddSequence(FrameworkElement? element)
    {
        if (element?.Tag is not LibraryItemViewModel item || BoxBeside(element) is not { } box || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which can rebuild the row
        if (_viewModel.AddSequence(item, box.Text))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private static TextBox? BoxBeside(FrameworkElement element) =>
        (element.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault();

    private void OnRemoveSequence(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AndroidSequenceViewModel input)
            _viewModel?.RemoveSequenceCommand.Execute(input);
    }

    private void OnAddKey(object sender, RoutedEventArgs e) => AddKey(sender as FrameworkElement);

    private void OnAddKeyKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AddKey(sender as FrameworkElement);
    }

    // The key box and the Add button share a panel; the sequence rides on their Tag. The box empties after a key goes in.
    private void AddKey(FrameworkElement? element)
    {
        if (element?.Tag is not AndroidSequenceViewModel sequence || _viewModel is null
            || (element.Parent as Panel)?.Children.OfType<ComboBox>().FirstOrDefault() is not { } box)
            return;
        var text = box.SelectedItem as string is { } picked && box.Text.Length == 0 ? picked : box.Text;
        if (_viewModel.AddKey(sequence, text))
        {
            box.SelectedItem = null;
            box.Text = "";
        }
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is KeyToken key)
            _viewModel?.RemoveKeyCommand.Execute(key);
    }

    // Alt+Up / Alt+Down on a key's ×: it trades places with its neighbour (like Send keys in the inspector).
    private void OnKeyMoveKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KeyToken key || _viewModel is null
            || (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0)
            return;
        var by = e.Key switch
        {
            Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Left => -1,
            Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Right => 1,
            _ => 0,
        };
        if (by != 0)
        {
            e.Handled = true;
            _viewModel.MoveKey(key, by);
        }
    }

    private static void CloseFlyouts(object sender)
    {
        if (sender is FrameworkElement element)
        {
            foreach (var popup in VisualTreeHelperPopups(element))
                popup.IsOpen = false;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryItemViewModel item)
            _viewModel?.RemoveCommand.Execute(item);
    }
}
