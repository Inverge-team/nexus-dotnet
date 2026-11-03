using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Models;
using Inverge.Nexus.Tests.Support;
using Inverge.Nexus.Transport;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class MessagingTests
{
    // Compared parsed: System.Text.Json escapes "+" as \u002B, which is the same JSON.
    private static void AssertJson(string expected, string? actual)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), actual);

    [Fact]
    public async Task A_WhatsApp_template_send_uses_the_API_field_names()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202,
            """{"id":"msg_1","status":"queued","channel":"whatsapp","segments":1,"mode":"live","price":{"amount":"0.0341","currency":"USD"}}"""));
        await using var __ = client;

        var sent = await client.Messaging.SendAsync(new SendMessage("whatsapp", "+9647501234567")
        {
            Template = "order_shipped",
            Language = "en_US",
            Variables = new Dictionary<string, string> { ["order_id"] = "A-91" },
        });

        Assert.Equal("msg_1", sent!.Id);
        Assert.Equal("0.0341", sent.Price!.Amount);
        var req = transport.Single();
        Assert.Equal(("POST", "/partner/messaging/messages"), (req.Method, req.Path));
        AssertJson("""{"channel":"whatsapp","to":"+9647501234567","templateKey":"order_shipped","templateLocale":"en_US","templateVariables":{"order_id":"A-91"}}""", req.Body);
        Assert.StartsWith("nxs-", req.Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task Structured_content_passes_through_and_a_given_key_is_used()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, "{}"));
        await using var __ = client;
        var content = new JsonObject { ["type"] = "photo", ["media"] = "tgf_abcdefghijkl", ["replyMarkup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() } };

        await client.Messaging.SendAsync(new SendMessage("telegram", "@alice") { Content = content, Body = "caption", TrackLinks = false, IdempotencyKey = "order-91" });
        await client.Messaging.SendAsync(new SendMessage("telegram", "@alice") { Content = content }); // the same node twice

        var first = transport.All()[0];
        Assert.Equal(content.ToJsonString(), first.Object["content"]!.ToJsonString());
        Assert.Equal("caption", first.Field("body"));
        Assert.Equal("false", first.Field("trackLinks"));
        Assert.Equal("order-91", first.Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task A_retried_send_keeps_its_idempotency_key()
    {
        var calls = 0;
        var (client, transport) = TestClient.Create(
            _ => ++calls == 1 ? RecordingTransport.Json(503, """{"error":{"code":"unavailable","message":"x"}}""") : RecordingTransport.Json(202, """{"id":"msg_2"}"""),
            o => { o.MaxRetries = 1; o.RetryBackoff = TimeSpan.Zero; });
        await using var __ = client;

        Assert.Equal("msg_2", (await client.Messaging.SendAsync(new SendMessage("whatsapp", "+1") { Body = "x" }))!.Id);
        var keys = transport.All().Select(r => r.Headers["Idempotency-Key"]).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
    }

    [Fact]
    public async Task A_rejection_throws_even_when_Silent()
    {
        var (client, _) = TestClient.Create(
            _ => RecordingTransport.Json(402, """{"error":{"code":"insufficient_balance","message":"Top up your wallet."}}"""),
            o => o.Silent = true);
        await using var __ = client;

        var e = await Assert.ThrowsAnyAsync<NexusApiException>(() => client.Messaging.SendAsync(new SendMessage("whatsapp", "+1") { Body = "x" }));
        Assert.Equal("insufficient_balance", e.Code);
    }

    [Fact]
    public async Task A_send_with_nothing_to_send_is_refused()
    {
        var (client, transport) = TestClient.Create();
        await using var __ = client;

        await Assert.ThrowsAsync<ArgumentException>(() => client.Messaging.SendAsync(new SendMessage("whatsapp", "+1")));
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task Get_and_events()
    {
        var (client, transport) = TestClient.Create(r => r.Path.EndsWith("/events", StringComparison.Ordinal)
            ? RecordingTransport.Json(200, """{"events":[{"id":"e1","type":"delivered","status":"delivered","occurredAt":"2026-11-04T10:00:00.000Z"}]}""")
            : RecordingTransport.Json(200, """{"id":"msg 1","status":"delivered","deliveredAt":"2026-11-04T10:00:00.000Z"}"""));
        await using var __ = client;

        var message = await client.Messaging.GetAsync("msg 1");
        var events = await client.Messaging.EventsAsync("msg 1");

        Assert.Equal("delivered", message!.Status);
        Assert.NotNull(message.DeliveredAt);
        Assert.Equal("delivered", Assert.Single(events).Type);
        Assert.Equal(
            new[] { "/partner/messaging/messages/msg%201", "/partner/messaging/messages/msg%201/events" },
            transport.All().Select(r => r.Path));
    }

    [Fact]
    public async Task Uploads_send_the_file_as_the_raw_body()
    {
        var (client, transport) = TestClient.Create(r => r.Path.StartsWith("/partner/messaging/media", StringComparison.Ordinal)
            ? RecordingTransport.Json(201, """{"mediaId":"123"}""")
            : RecordingTransport.Json(201, """{"uploadId":"tgf_x","size":4}"""));
        await using var __ = client;

        var media = await client.Messaging.UploadMediaAsync(Encoding.UTF8.GetBytes("%PDF"), "application/pdf", "invoice 1.pdf");
        var path = Path.Combine(Path.GetTempPath(), "nx-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            Assert.Equal("tgf_x", (await client.Messaging.UploadTelegramFileAsync(path))!.UploadId);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal("123", media!.MediaId);
        var (wa, tg) = (transport.All()[0], transport.All()[1]);
        Assert.Equal("/partner/messaging/media?filename=invoice%201.pdf", wa.Path);
        Assert.Equal("application/pdf", wa.Headers["Content-Type"]);
        Assert.Equal("%PDF", wa.Body);
        Assert.StartsWith("/partner/messaging/telegram/uploads?filename=nx-", tg.Path);
        Assert.Equal("image/png", tg.Headers["Content-Type"]);
    }

    [Fact]
    public async Task Verifications_start_and_check_are_never_retried()
    {
        var (client, transport) = TestClient.Create(r => r.Path.EndsWith("/check", StringComparison.Ordinal)
                ? RecordingTransport.Json(503, "{}")
                : RecordingTransport.Json(201, """{"id":"ver_1","status":"pending","maxAttempts":5}"""),
            o => { o.MaxRetries = 3; o.RetryBackoff = TimeSpan.Zero; });
        await using var __ = client;

        var v = await client.Messaging.Verifications.StartAsync(new StartVerification("+9647501234567") { CodeLength = 6, SmsFallback = true });
        await Assert.ThrowsAnyAsync<NexusException>(() => client.Messaging.Verifications.CheckAsync(v!.Id, "123456"));

        var (start, check) = (transport.All()[0], transport.All()[1]);
        Assert.Equal(2, transport.Count);
        AssertJson("""{"to":"+9647501234567","codeLength":6,"fallback":"sms"}""", start.Body);
        Assert.Equal(("/partner/messaging/verifications/ver_1/check", "123456"), (check.Path, check.Field("code")));
    }

    [Fact]
    public async Task Start_carries_the_channel_plan_and_template()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(201, """{"id":"ver_2"}"""));
        await using var __ = client;

        await client.Messaging.Verifications.StartAsync(new StartVerification("+9647501234567")
        {
            Channels = new[] { "telegram", "whatsapp" },
            WhatsAppTemplate = "acme_login",
            WhatsAppLanguage = "ar",
        });

        AssertJson("""{"to":"+9647501234567","channels":["telegram","whatsapp"],"whatsappTemplate":"acme_login","whatsappLanguage":"ar"}""", transport.Single().Body);
    }

    [Fact]
    public async Task A_check_reports_validity()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(200, """{"id":"ver_1","status":"approved","valid":true,"attemptsRemaining":4}"""));
        await using var __ = client;

        var r = await client.Messaging.Verifications.CheckAsync("ver_1", "123456");
        Assert.True(r!.Valid);
        Assert.Equal(4, r.AttemptsRemaining);
    }

    [Fact]
    public async Task Switched_off_makes_no_call()
    {
        var (client, transport) = TestClient.Create(configure: o => o.Services[NexusServices.Messaging] = false);
        await using var __ = client;

        Assert.Null(await client.Messaging.SendAsync(new SendMessage("whatsapp", "+1") { Body = "x" }));
        Assert.Null(await client.Messaging.UploadMediaAsync(new byte[] { 1 }, "image/jpeg"));
        Assert.Null(await client.Messaging.Verifications.StartAsync(new StartVerification("+1")));
        Assert.Empty(await client.Messaging.EventsAsync("m"));
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task Runs_directly_even_behind_a_background_dispatcher()
    {
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"id":"msg_3"}"""));
        await using var client = TestClient.Create(transport);
        await using var bg = (NexusClient)client.Background();

        Assert.Equal("msg_3", (await bg.Messaging.SendAsync(new SendMessage("telegram", "@a") { Body = "x" }))!.Id);
    }
}
