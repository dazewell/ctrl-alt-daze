using CtrlAltDaze.Core;
using CtrlAltDaze.Core.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace CtrlAltDaze.Editor.Inspector;

/// <summary>What the guide gives the step: the saved key's name (the bot's user name) and the chat it found.</summary>
internal sealed record TelegramSetupResult(string KeyName, TelegramChat Chat);

/// <summary>
/// "Set up Telegram" (D-158): make a bot with BotFather, paste its key (checked with getMe), press Start in the bot's chat
/// through a link with a one-time code (found with getUpdates, only while this dialog is open), send a test message.
/// "Use this chat" saves the key in Credential Manager; the step gets only its name and the chat's number.
/// </summary>
internal sealed class TelegramSetupDialog : IDisposable
{
    private static readonly TimeSpan ListenFor = TimeSpan.FromMinutes(10);

    private readonly TelegramApi _api;
    private readonly PasswordBox _key = new() { PlaceholderText = "123456789:AAE…", Header = "Bot key" };
    private readonly Button _check = new() { Content = "Check key" };
    private readonly TextBlock _keyStatus = Note("");
    private readonly StackPanel _hello = new() { Spacing = 8, Opacity = 0.5 };
    private readonly Button _openBot = new() { Content = "Open my bot", IsEnabled = false };
    private readonly HyperlinkButton _openGroup = new() { Content = "Use a group instead", IsEnabled = false };
    private readonly TextBlock _chatStatus = Note("");
    private readonly StackPanel _test = new() { Spacing = 8, Opacity = 0.5 };
    private readonly Button _send = new() { Content = "Send a test message", IsEnabled = false };
    private readonly TextBlock _testStatus = Note("");
    private ContentDialog? _dialog;
    private CancellationTokenSource? _listen;
    private string? _checkedKey;
    private string? _botName;
    private TelegramChat? _chat;

    private TelegramSetupDialog(TelegramApi api) => _api = api;

    public static async Task<TelegramSetupResult?> ShowAsync(XamlRoot root)
    {
        // The editor's own client, only while the guide is open: nothing stays behind (idle = zero).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        using var setup = new TelegramSetupDialog(new TelegramApi(http));
        var dialog = setup.Build(root);
        return await dialog.ShowAsync() == ContentDialogResult.Primary && setup._botName is { } bot && setup._chat is { } chat
            ? new TelegramSetupResult(bot, chat)
            : null;
    }

    public void Dispose() => StopListening();

    private ContentDialog Build(XamlRoot root)
    {
        _check.Click += async (_, _) => await CheckKeyAsync();
        _key.PasswordChanged += (_, _) =>
        {
            if (_checkedKey is not null && _key.Password.Trim() != _checkedKey)
                Reset("");
        };
        _openBot.Click += async (_, _) => await OpenBotAsync(group: false);
        _openGroup.Click += async (_, _) => await OpenBotAsync(group: true);
        _send.Click += async (_, _) => await SendTestAsync();

        _hello.Children.Add(Heading("2 · Say hello to it"));
        _hello.Children.Add(Note("Click Open my bot and press Start in Telegram. Bots can't write to you until you do. "
            + "For a group, use a group instead and pick the group."));
        _hello.Children.Add(Row(_openBot, _openGroup));
        _hello.Children.Add(_chatStatus);

        _test.Children.Add(Heading("3 · Test it"));
        _test.Children.Add(Note("It should arrive in a few seconds."));
        _test.Children.Add(_send);
        _test.Children.Add(_testStatus);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Heading("1 · Make your bot"));
        content.Children.Add(Note("Open BotFather, send /newbot, give it any name, then a user name that ends in bot (like daze_pc_bot). "
            + "BotFather answers with a key. Paste it here."));
        content.Children.Add(new HyperlinkButton { Content = "Open BotFather", NavigateUri = new Uri(TelegramApi.BotFatherUrl) });
        content.Children.Add(_key);
        content.Children.Add(_check);
        content.Children.Add(_keyStatus);
        content.Children.Add(Note("The key is saved in Windows Credential Manager, never in your flows. Anyone with it can send messages as your "
            + "bot: keep it private. If it gets out, send /revoke to BotFather and set up again."));
        content.Children.Add(new Border { Height = 8 });
        content.Children.Add(_hello);
        content.Children.Add(new Border { Height = 8 });
        content.Children.Add(_test);

        _dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Set up Telegram",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Use this chat",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        _dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = "Check the key first.";
            if (_checkedKey is null || _botName is null || !SavedKeys.TrySave(_botName, _checkedKey, $"Telegram bot @{_botName} for {AppInfo.ProductName}", out problem))
            {
                args.Cancel = true;
                _testStatus.Text = problem ?? "";
            }
        };
        return _dialog;
    }

    private async Task CheckKeyAsync()
    {
        var key = _key.Password.Trim();
        Reset("Checking…");
        _check.IsEnabled = false;
        try
        {
            _botName = await _api.BotNameAsync(key, CancellationToken.None);
            _checkedKey = key;
            _keyStatus.Text = $"Connected to @{_botName}.";
            _hello.Opacity = 1;
            _openBot.IsEnabled = _openGroup.IsEnabled = true;
        }
        catch (TelegramException e)
        {
            _keyStatus.Text = e.Message;
        }
        finally
        {
            _check.IsEnabled = true;
        }
    }

    private async Task OpenBotAsync(bool group)
    {
        if (_checkedKey is not { } key || _botName is not { } bot)
            return;
        StopListening();
        var code = TelegramApi.NewStartCode();
        var listen = _listen = new CancellationTokenSource(ListenFor);
        _chatStatus.Text = group ? "Pick the group in Telegram. Waiting for it…" : "Press Start in Telegram. Waiting for it…";
        await Launcher.LaunchUriAsync(new Uri(group ? TelegramApi.GroupLink(bot, code) : TelegramApi.StartLink(bot, code)));
        var offset = new Ref<long>(0);
        try
        {
            while (!listen.IsCancellationRequested)
            {
                if (await _api.FindStartAsync(key, code, offset, 20, listen.Token) is { } chat)
                {
                    _chat = chat;
                    _chatStatus.Text = $"Found {chat.Title}.";
                    _test.Opacity = 1;
                    _send.IsEnabled = true;
                    if (_dialog is not null)
                        _dialog.IsPrimaryButtonEnabled = true;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (TelegramException e) when (!listen.IsCancellationRequested)
        {
            _chatStatus.Text = e.Message;
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        // Ten minutes and no Start, unless a newer click or Cancel took over.
        if (ReferenceEquals(_listen, listen) && _chat is null)
            _chatStatus.Text = "Nothing came from Telegram. Click Open my bot and press Start again.";
    }

    private async Task SendTestAsync()
    {
        if (_checkedKey is not { } key || _chat is not { } chat)
            return;
        _send.IsEnabled = false;
        _testStatus.Text = "Sending…";
        try
        {
            await _api.SendAsync(key, chat.Id, $"Hello from {AppInfo.ProductName}. Your flows can send messages here.", silent: false, CancellationToken.None);
            _testStatus.Text = "Sent. Check Telegram.";
        }
        catch (TelegramException e)
        {
            _testStatus.Text = e.Message;
        }
        finally
        {
            _send.IsEnabled = true;
        }
    }

    /// <summary>A new or changed key: everything after step 1 starts over.</summary>
    private void Reset(string keyStatus)
    {
        StopListening();
        _checkedKey = _botName = null;
        _chat = null;
        _keyStatus.Text = keyStatus;
        _chatStatus.Text = _testStatus.Text = "";
        _hello.Opacity = _test.Opacity = 0.5;
        _openBot.IsEnabled = _openGroup.IsEnabled = _send.IsEnabled = false;
        if (_dialog is not null)
            _dialog.IsPrimaryButtonEnabled = false;
    }

    private void StopListening()
    {
        _listen?.Cancel();
        _listen?.Dispose();
        _listen = null;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var item in items)
            row.Children.Add(item);
        return row;
    }
}
