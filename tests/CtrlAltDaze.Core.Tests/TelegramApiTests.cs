using System.Net;
using System.Text.Json;
using CtrlAltDaze.Core.Telegram;

namespace CtrlAltDaze.Core.Tests;

public sealed class TelegramApiTests
{
    private const string Key = "123456789:AAEhBOweik6ad9r_QXMENQjcrGbqCr4K-ra";

    private sealed class FakeHandler(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        public Uri? Address { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Address = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Failed: {request.RequestUri}");
    }

    [Theory]
    [InlineData(Key, true)]
    [InlineData("123:short", false)]
    [InlineData("bot123456789:AAEhBOweik6ad9r_QXMENQjcrGbqCr4K-ra", false)]
    [InlineData(" " + Key, false)]
    public void KeysHaveBotFathersShape(string key, bool ok) => Assert.Equal(ok, TelegramApi.LooksLikeKey(key));

    [Theory]
    [InlineData("123456789", true)]
    [InlineData("-1001234567890", true)]
    [InlineData("@my_channel", true)]
    [InlineData("@ab", false)]
    [InlineData("my chat", false)]
    [InlineData("", false)]
    public void ChatsAreNumbersOrChannelNames(string chat, bool ok) => Assert.Equal(ok, TelegramApi.LooksLikeChat(chat));

    [Fact]
    public void ANumberChatIsSentAsANumberAndTextStaysPlain()
    {
        Assert.Equal("{\"chat_id\":-100123,\"text\":\"*hi* \\u003Cb\\u003E\",\"disable_notification\":true}",
            TelegramApi.SendBody("-100123", "*hi* <b>", silent: true));
        Assert.Equal("{\"chat_id\":\"@news_room\",\"text\":\"x\"}", TelegramApi.SendBody("@news_room", "x", silent: false));
    }

    [Fact]
    public void StartFindsTheChatWithTheCodeAndMovesPastEveryUpdate()
    {
        using var json = JsonDocument.Parse("""
            {"ok":true,"result":[
              {"update_id":10,"message":{"chat":{"id":5,"type":"private","first_name":"Eve"},"text":"/start OTHERCODE"}},
              {"update_id":11,"message":{"chat":{"id":42,"type":"private","first_name":"Dima","last_name":"B"},"text":"/start ABC123"}},
              {"update_id":12,"message":{"chat":{"id":-77,"type":"group","title":"Home"},"text":"hello"}}
            ]}
            """);
        var (chat, next) = TelegramApi.FindStart(json.RootElement, "ABC123");
        Assert.Equal(new TelegramChat("42", "Dima B (private chat)"), chat);
        Assert.Equal(13, next);
    }

    [Fact]
    public void AGroupStartNamesTheBot()
    {
        using var json = JsonDocument.Parse("""
            {"ok":true,"result":[{"update_id":1,"message":{"chat":{"id":-1001,"type":"supergroup","title":"Family"},"text":"/start@daze_pc_bot ABC123"}}]}
            """);
        Assert.Equal(new TelegramChat("-1001", "Family (group)"), TelegramApi.FindStart(json.RootElement, "ABC123").Chat);
    }

    [Fact]
    public void NoMatchIsNull()
    {
        using var json = JsonDocument.Parse("""{"ok":true,"result":[]}""");
        Assert.Equal((null, 0), TelegramApi.FindStart(json.RootElement, "ABC123"));
    }

    [Fact]
    public async Task SendPostsJsonToTheBotsMethod()
    {
        using var handler = new FakeHandler(HttpStatusCode.OK, """{"ok":true,"result":{}}""");
        using var http = new HttpClient(handler);
        await new TelegramApi(http).SendAsync(Key, "42", "hi", silent: false, TestContext.Current.CancellationToken);
        Assert.Equal($"https://api.telegram.org/bot{Key}/sendMessage", handler.Address!.ToString());
        Assert.Equal("{\"chat_id\":42,\"text\":\"hi\"}", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized", "doesn't know this bot key")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden: bot can't initiate conversation with a user", "press Start")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user", "blocked or removed")]
    [InlineData(HttpStatusCode.BadRequest, "Bad Request: chat not found", "can't find this chat")]
    [InlineData(HttpStatusCode.Conflict, "Conflict: can't use getUpdates method while webhook is active", "webhook")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too Many Requests: retry after 5", "too many messages")]
    public async Task FailuresAreOurSentencesAndNeverHoldTheKey(HttpStatusCode status, string description, string expected)
    {
        using var handler = new FakeHandler(status, $$"""{"ok":false,"error_code":{{(int)status}},"description":"{{description}} {{Key}}"}""");
        using var http = new HttpClient(handler);
        var e = await Assert.ThrowsAsync<TelegramException>(() => new TelegramApi(http).SendAsync(Key, "42", "hi", false, TestContext.Current.CancellationToken));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AAEh", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkErrorNeverRepeatsTheAddress()
    {
        using var http = new HttpClient(new FailingHandler());
        var e = await Assert.ThrowsAsync<TelegramException>(() => new TelegramApi(http).BotNameAsync(Key, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("AAEh", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadKeyIsRefusedBeforeAnythingIsSent()
    {
        using var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramException>(() => new TelegramApi(http).BotNameAsync("nope", TestContext.Current.CancellationToken));
        Assert.Null(handler.Address);
    }

    [Fact]
    public async Task BotNameAndStartUseTheAnswer()
    {
        using var me = new FakeHandler(HttpStatusCode.OK, """{"ok":true,"result":{"id":1,"is_bot":true,"username":"daze_pc_bot"}}""");
        using var http = new HttpClient(me);
        Assert.Equal("daze_pc_bot", await new TelegramApi(http).BotNameAsync(Key, TestContext.Current.CancellationToken));

        using var updates = new FakeHandler(HttpStatusCode.OK, """{"ok":true,"result":[{"update_id":7,"message":{"chat":{"id":9,"type":"private"},"text":"/start C0DE"}}]}""");
        using var http2 = new HttpClient(updates);
        var offset = new Ref<long>(3);
        Assert.Equal(new TelegramChat("9", "Private chat"), await new TelegramApi(http2).FindStartAsync(Key, "C0DE", offset, 20, TestContext.Current.CancellationToken));
        Assert.Equal(8, offset.Value);
        Assert.Contains("\"offset\":3", updates.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void StartCodesFitTelegramsRules()
    {
        var code = TelegramApi.NewStartCode();
        Assert.Matches("^[A-Za-z0-9]{16}$", code);
        Assert.Equal($"https://t.me/daze_pc_bot?start={code}", TelegramApi.StartLink("daze_pc_bot", code));
    }
}
