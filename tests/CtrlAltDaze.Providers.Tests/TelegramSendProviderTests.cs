using System.Net;
using CtrlAltDaze.Providers.Http;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

public sealed class TelegramSendProviderTests : IDisposable
{
    // Made up at run time: a token-shaped literal in source trips secret scanning (alert #1, 2026-11-26).
    private static readonly string Key = string.Concat("123456789", ":", new string('x', 35));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public Uri? Address { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Address = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK ? """{"ok":true,"result":{}}""" : """{"ok":false,"description":"Forbidden"}"""),
            };
        }
    }

    private sealed class FakeCredentials : ICredentialStore
    {
        public Credential? Read(string name) => name == "telegram" ? new Credential("", Key + " ") : null;
    }

    private readonly FakeHandler _handler = new();
    private readonly TelegramSendProvider _provider;

    public TelegramSendProviderTests() => _provider = new TelegramSendProvider(_handler, new FakeCredentials());

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task SendsTheMessageWithTheSavedKey()
    {
        await _provider.RunAsync(("bot", "telegram"), ("chat", "42"), ("text", "Hi there"), ("silent", "true"));
        Assert.Equal($"https://api.telegram.org/bot{Key}/sendMessage", _handler.Address!.ToString());
        Assert.Equal("{\"chat_id\":42,\"text\":\"Hi there\",\"disable_notification\":true}", _handler.Body);
    }

    [Fact]
    public async Task AMissingKeySaysHowToSaveIt()
    {
        var e = await Assert.ThrowsAsync<StepFailedException>(() => _provider.RunAsync(("bot", "other"), ("chat", "42"), ("text", "x")));
        Assert.Contains("Set up Telegram", e.Message, StringComparison.Ordinal);
        Assert.Contains("CtrlAltDaze:other", e.Message, StringComparison.Ordinal);
        Assert.Null(_handler.Address);
    }

    [Theory]
    [InlineData("my chat", "x", "Set up Telegram")]
    [InlineData("42", "   ", "")] // an empty message: RequiredText's own sentence
    public async Task BadSettingsFailBeforeSending(string chat, string text, string expected)
    {
        var e = await Assert.ThrowsAsync<StepFailedException>(() => _provider.RunAsync(("bot", "telegram"), ("chat", chat), ("text", text)));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
        Assert.Null(_handler.Address);
    }

    [Fact]
    public async Task ATooLongMessageIsRefused()
    {
        var e = await Assert.ThrowsAsync<StepFailedException>(() => _provider.RunAsync(("bot", "telegram"), ("chat", "42"), ("text", new string('a', 4097))));
        Assert.Contains("4,096", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramsRefusalIsASentenceWithoutTheKey()
    {
        _handler.Status = HttpStatusCode.Forbidden;
        var e = await Assert.ThrowsAsync<StepFailedException>(() => _provider.RunAsync(("bot", "telegram"), ("chat", "42"), ("text", "x")));
        Assert.Contains("press Start", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxxxxx", e.Message, StringComparison.Ordinal);
    }
}
