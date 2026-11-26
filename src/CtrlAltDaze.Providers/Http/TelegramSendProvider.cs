using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Telegram;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Http;

/// <summary>
/// <c>telegram.send</c> (D-158): one <c>sendMessage</c> as plain text. The bot's key is the secret of the saved
/// credential <c>bot</c> names; it never reaches the step's error, the log or the YAML (D-014).
/// </summary>
public sealed class TelegramSendProvider(HttpMessageHandler handler, ICredentialStore credentials) : IStepProvider, IDisposable
{
    // As http.request: one client for the engine's life, its idle connection closes after a minute (D-009).
    private readonly HttpClient _client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public TelegramSendProvider(ICredentialStore credentials)
        : this(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1), UseCookies = false, AllowAutoRedirect = false,
        }, credentials)
    {
    }

    public string Id => ProviderCatalog.TelegramSendId;

    public void Dispose() => _client.Dispose();

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var bot = context.RequiredText("bot").Trim();
        var chat = context.RequiredText("chat").Trim();
        var text = context.RequiredText("text");
        if (text.Trim().Length == 0)
            throw new StepFailedException("The message is empty.");
        if (text.Length > TelegramApi.MaxText)
            throw new StepFailedException($"The message has {text.Length:N0} characters; Telegram takes at most {TelegramApi.MaxText:N0}.");
        if (!TelegramApi.LooksLikeChat(chat))
            throw new StepFailedException("The chat isn't a chat's number or a channel's @name. Run Set up Telegram to fill it in.");
        var credential = credentials.Read(bot)
            ?? throw new StepFailedException($"No saved bot key called \"{bot}\". Run Set up Telegram in the editor, or save the key in "
                + $"Windows Credential Manager as a generic credential named {WindowsCredentials.TargetPrefix}{bot}.");
        try
        {
            await new TelegramApi(_client).SendAsync(credential.Secret.Trim(), chat, text, context.Boolean("silent", fallback: false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TelegramException e)
        {
            throw new StepFailedException(e.Message);
        }
    }
}
