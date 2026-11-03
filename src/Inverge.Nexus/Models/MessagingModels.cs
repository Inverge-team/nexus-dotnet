using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>
/// One message to send. Give <see cref="Body"/> (text), <see cref="Template"/>
/// (an approved template) or <see cref="Content"/> (structured content).
/// </summary>
/// <example>
/// <code>
/// await nexus.Messaging.SendAsync(new SendMessage("whatsapp", "+9647501234567")
/// {
///     Template = "order_shipped",
///     Language = "en_US",
///     Variables = new Dictionary&lt;string, string&gt; { ["order_id"] = "A-91" },
/// });
/// </code>
/// </example>
public sealed class SendMessage
{
    /// <summary>A message on <paramref name="channel"/> to <paramref name="to"/>.</summary>
    public SendMessage(string channel, string to)
    {
        Channel = channel;
        To = to;
    }

    /// <summary><c>whatsapp</c>, <c>telegram</c>, <c>sms</c>, <c>messenger</c>, <c>instagram</c>.</summary>
    public string Channel { get; }

    /// <summary>E.164 for WhatsApp/SMS; a chat id or <c>@username</c> for Telegram.</summary>
    public string To { get; }

    /// <summary>Plain text — or the caption when <see cref="Content"/> is media.</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Structured content, passed through as given and validated per channel
    /// (WhatsApp keys it by <c>kind</c>, Telegram by <c>type</c>). A
    /// <see cref="JsonObject"/> or a dictionary; an anonymous object works too,
    /// through reflection (avoid it under trimming/AOT).
    /// </summary>
    public object? Content { get; init; }

    /// <summary>A registered sender (SMS sender ID, Telegram bot <c>@username</c>). Optional on WhatsApp.</summary>
    public string? Sender { get; init; }

    /// <summary>An approved template by name.</summary>
    public string? Template { get; init; }

    /// <summary>Template language/locale, e.g. <c>en_US</c>, <c>ar</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Values for the template's named variables.</summary>
    public IReadOnlyDictionary<string, string>? Variables { get; init; }

    /// <summary><c>transactional</c> (default), <c>promotional</c> or <c>otp</c>.</summary>
    public string? MessageClass { get; init; }

    /// <summary>When to send. Omit to send now.</summary>
    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>Seconds the message stays worth delivering (60–259200).</summary>
    public int? ValidityPeriodSec { get; init; }

    /// <summary>Echoed back on reads and webhooks.</summary>
    public object? Metadata { get; init; }

    /// <summary>Rewrite links into tracked short links (SMS and WhatsApp).</summary>
    public bool? TrackLinks { get; init; }

    /// <summary>Your end user's IP, for abuse limits.</summary>
    public string? ClientIp { get; init; }

    /// <summary>Dedupe key. Generated when omitted, so a retried send never doubles.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Options for a one-time-code verification.</summary>
public sealed class StartVerification
{
    /// <summary>A verification for the phone number <paramref name="to"/> (E.164).</summary>
    public StartVerification(string to) => To = to;

    /// <summary>E.164.</summary>
    public string To { get; }

    /// <summary>
    /// Channels to try, in order (<c>whatsapp</c>, <c>telegram</c>, <c>sms</c>). Default: WhatsApp,
    /// then Telegram — the code moves on when a channel refuses it or hasn't delivered it within
    /// <see cref="FallbackAfter"/>.
    /// </summary>
    public IReadOnlyList<string>? Channels { get; init; }

    /// <summary>WhatsApp authentication template to send (default: the platform's).</summary>
    public string? WhatsAppTemplate { get; init; }

    /// <summary>The template's language, e.g. <c>en_US</c>, <c>ar</c>.</summary>
    public string? WhatsAppLanguage { get; init; }

    /// <summary>Digits in the code (4–8).</summary>
    public int? CodeLength { get; init; }

    /// <summary>Seconds the code stays valid.</summary>
    public int? Ttl { get; init; }

    /// <summary>Also try SMS last (only where SMS is available).</summary>
    public bool SmsFallback { get; init; }

    /// <summary>Seconds each channel gets to deliver before the next is tried.</summary>
    public int? FallbackAfter { get; init; }

    /// <summary>The SMS sender for the fallback.</summary>
    public string? SmsSender { get; init; }

    /// <summary><c>{{code}}</c> is replaced.</summary>
    public string? SmsTemplate { get; init; }

    /// <summary>A verified Telegram channel to sign the code with (without @).</summary>
    public string? SenderUsername { get; init; }

    /// <summary>Wrong codes allowed before the verification fails (1–10).</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>Echoed back on reads.</summary>
    public object? Metadata { get; init; }
}

/// <summary>A customer-facing price — a decimal string, never a float.</summary>
public sealed class MessagePrice
{
    internal MessagePrice(JsonObject node)
    {
        Amount = node.Str("amount") ?? "0";
        Currency = node.Str("currency") ?? string.Empty;
    }

    /// <summary>The amount, as a decimal string.</summary>
    public string Amount { get; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; }

    internal static MessagePrice? From(JsonNode? node) => node is JsonObject o ? new MessagePrice(o) : null;
}

/// <summary>What a send returned: the message is queued; delivery follows.</summary>
public sealed class MessageSendResult
{
    internal MessageSendResult(JsonObject node)
    {
        Raw = node;
        Id = node.Str("id") ?? string.Empty;
        Status = node.Str("status");
        Channel = node.Str("channel");
        Segments = node.Int("segments");
        Mode = node.Str("mode");
        Price = MessagePrice.From(node.Prop("price"));
        Warnings = node.Strings("warnings");
    }

    /// <summary>The response as returned by the API.</summary>
    public JsonObject Raw { get; }

    /// <summary>The id.</summary>
    public string Id { get; }

    /// <summary>The current status.</summary>
    public string? Status { get; }

    /// <summary>The channel.</summary>
    public string? Channel { get; }

    /// <summary>Billable segments (SMS); 1 elsewhere.</summary>
    public int Segments { get; }

    /// <summary><c>live</c>, <c>sandbox</c> or <c>mock</c>.</summary>
    public string? Mode { get; }

    /// <summary>What the message costs you, or null while not yet priced.</summary>
    public MessagePrice? Price { get; }

    /// <summary>Non-fatal notes about the send.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>A sent message's current state.</summary>
public sealed class MessageInfo
{
    internal MessageInfo(JsonObject node)
    {
        Raw = node;
        Id = node.Str("id") ?? string.Empty;
        Channel = node.Str("channel");
        Status = node.Str("status");
        To = node.Str("to");
        Sender = node.Str("sender");
        Body = node.Str("body");
        Segments = node.Int("segments");
        Country = node.Str("country");
        Mode = node.Str("mode");
        Price = MessagePrice.From(node.Prop("price"));
        FailureCode = node.Str("failureCode");
        FailureReason = node.Str("failureReason");
        CreatedAt = node.Timestamp("createdAt");
        SubmittedAt = node.Timestamp("submittedAt");
        DeliveredAt = node.Timestamp("deliveredAt");
        ScheduledAt = node.Timestamp("scheduledAt");
        ExpiresAt = node.Timestamp("expiresAt");
        Metadata = node.Prop("metadata");
    }

    /// <summary>The response as returned by the API.</summary>
    public JsonObject Raw { get; }

    /// <summary>The id.</summary>
    public string Id { get; }

    /// <summary>The channel.</summary>
    public string? Channel { get; }

    /// <summary>queued, processing, submitted, sent, delivered, failed, expired, rejected, suppressed.</summary>
    public string? Status { get; }

    /// <summary>The recipient.</summary>
    public string? To { get; }

    /// <summary>The sender it went from.</summary>
    public string? Sender { get; }

    /// <summary>The text, while bodies are retained.</summary>
    public string? Body { get; }

    /// <summary>Billable segments (SMS); 1 elsewhere.</summary>
    public int Segments { get; }

    /// <summary>Destination country (ISO 3166-1 alpha-2).</summary>
    public string? Country { get; }

    /// <summary><c>live</c>, <c>sandbox</c> or <c>mock</c>.</summary>
    public string? Mode { get; }

    /// <summary>What the message costs you, or null while not yet priced.</summary>
    public MessagePrice? Price { get; }

    /// <summary>Why it failed, as a stable code.</summary>
    public string? FailureCode { get; }

    /// <summary>Why it failed, in words.</summary>
    public string? FailureReason { get; }

    /// <summary>When it was accepted.</summary>
    public DateTimeOffset? CreatedAt { get; }

    /// <summary>When it was handed to the network.</summary>
    public DateTimeOffset? SubmittedAt { get; }

    /// <summary>When delivery was confirmed.</summary>
    public DateTimeOffset? DeliveredAt { get; }

    /// <summary>When it is scheduled to send.</summary>
    public DateTimeOffset? ScheduledAt { get; }

    /// <summary>When it stops being worth delivering.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Your metadata, echoed back.</summary>
    public JsonNode? Metadata { get; }
}

/// <summary>One step of a message's delivery timeline.</summary>
public sealed class MessageEvent
{
    internal MessageEvent(JsonObject node)
    {
        Id = node.Str("id");
        Type = node.Str("type");
        Status = node.Str("status");
        OccurredAt = node.Timestamp("occurredAt");
        Data = node.Prop("data");
    }

    /// <summary>The id.</summary>
    public string? Id { get; }

    /// <summary>The event type, e.g. <c>delivered</c>.</summary>
    public string? Type { get; }

    /// <summary>The current status.</summary>
    public string? Status { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset? OccurredAt { get; }

    /// <summary>Event details.</summary>
    public JsonNode? Data { get; }
}

/// <summary>A file uploaded for a send.</summary>
public sealed class MessagingUpload
{
    internal MessagingUpload(JsonObject node)
    {
        Raw = node;
        MediaId = node.Str("mediaId");
        UploadId = node.Str("uploadId");
        Size = node.Long("size");
        ExpiresAt = node.Timestamp("expiresAt");
    }

    /// <summary>The response as returned by the API.</summary>
    public JsonObject Raw { get; }

    /// <summary>WhatsApp: send the file as <c>{ "id": MediaId }</c>.</summary>
    public string? MediaId { get; }

    /// <summary>Telegram: send <c>UploadId</c> (<c>tgf_…</c>) as the media.</summary>
    public string? UploadId { get; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; }

    /// <summary>When it stops being worth delivering.</summary>
    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary>A one-time-code verification.</summary>
public sealed class Verification
{
    internal Verification(JsonObject node)
    {
        Raw = node;
        Id = node.Str("id") ?? string.Empty;
        To = node.Str("to");
        Status = node.Str("status");
        Channel = node.Str("channel");
        Fallback = node.Str("fallback");
        FellBackAt = node.Timestamp("fellBackAt");
        Attempts = node.Int("attempts");
        MaxAttempts = node.Int("maxAttempts");
        ExpiresAt = node.Timestamp("expiresAt");
        ApprovedAt = node.Timestamp("approvedAt");
        Valid = node.Flag("valid");
        AttemptsRemaining = node.Int("attemptsRemaining");
    }

    /// <summary>The response as returned by the API.</summary>
    public JsonObject Raw { get; }

    /// <summary>The id.</summary>
    public string Id { get; }

    /// <summary>The recipient.</summary>
    public string? To { get; }

    /// <summary>pending, approved, failed, expired, canceled.</summary>
    public string? Status { get; }

    /// <summary>The channel.</summary>
    public string? Channel { get; }

    /// <summary><c>sms</c> when SMS fallback was requested.</summary>
    public string? Fallback { get; }

    /// <summary>When the code went out by SMS instead.</summary>
    public DateTimeOffset? FellBackAt { get; }

    /// <summary>Codes checked so far.</summary>
    public int Attempts { get; }

    /// <summary>Codes allowed.</summary>
    public int MaxAttempts { get; }

    /// <summary>When it stops being worth delivering.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>When the right code was entered.</summary>
    public DateTimeOffset? ApprovedAt { get; }

    /// <summary>After a check: whether the code matched.</summary>
    public bool Valid { get; }

    /// <summary>After a check: tries left.</summary>
    public int AttemptsRemaining { get; }
}
