using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>
/// Messaging — send on WhatsApp, Telegram, SMS and the other channels, read a
/// sent message's delivery, upload a file to attach, and run OTP verifications.
/// </summary>
/// <remarks>
/// Send-only: templates, numbers, senders, webhooks, campaigns and the inbox
/// are managed in the console.
/// <para>
/// Messaging always runs directly and always throws on failure — whatever the
/// dispatcher or <c>Silent</c> setting — because a send that failed must not
/// look like one that worked. Errors are <see cref="NexusApiException"/>s whose
/// code says why (<c>content_rejected</c>, <c>insufficient_balance</c>, …).
/// When messaging is switched off (<c>NEXUS_MESSAGING_ENABLED=false</c>) every
/// call returns <see langword="null"/> without a request.
/// </para>
/// </remarks>
public sealed class MessagingResource : NexusResource
{
    private static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".gif"] = "image/gif",
        [".mp4"] = "video/mp4", [".3gp"] = "video/3gpp", [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg", [".amr"] = "audio/amr", [".pdf"] = "application/pdf", [".txt"] = "text/plain",
        [".csv"] = "text/csv", [".zip"] = "application/zip",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    internal MessagingResource(NexusClient client) : base(client)
    {
        Verifications = new VerificationsResource(client);
    }

    /// <summary>OTP codes over Telegram, with SMS fallback.</summary>
    public VerificationsResource Verifications { get; }

    /// <summary>
    /// Send a message. Resolves once it is queued; delivery is asynchronous —
    /// read it back with <see cref="GetAsync"/> / <see cref="EventsAsync"/>.
    /// Safe to retry: every send carries an idempotency key.
    /// </summary>
    public async Task<MessageSendResult?> SendAsync(SendMessage message, CancellationToken cancellationToken = default)
    {
        var request = Payloads.MessagingSend(Body(message), message.IdempotencyKey ?? NewIdempotencyKey());
        return await Client.ExecuteMessagingAsync(request, cancellationToken).ConfigureAwait(false) is JsonObject o
            ? new MessageSendResult(o)
            : null;
    }

    /// <summary>A sent message's current status, price and timestamps.</summary>
    public async Task<MessageInfo?> GetAsync(string messageId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(messageId, nameof(messageId));
        return await Client.ExecuteMessagingAsync(Payloads.MessagingGet(messageId), cancellationToken).ConfigureAwait(false) is JsonObject o
            ? new MessageInfo(o)
            : null;
    }

    /// <summary>The delivery timeline: queued → submitted → sent → delivered.</summary>
    public async Task<IReadOnlyList<MessageEvent>> EventsAsync(string messageId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(messageId, nameof(messageId));
        var data = await Client.ExecuteMessagingAsync(Payloads.MessagingEvents(messageId), cancellationToken).ConfigureAwait(false);
        var list = new List<MessageEvent>();
        foreach (var item in data.Items("events"))
        {
            if (item is JsonObject o)
            {
                list.Add(new MessageEvent(o));
            }
        }

        return list;
    }

    /// <summary>Upload bytes for WhatsApp; send them as <c>{ "id": result.MediaId }</c>.</summary>
    public Task<MessagingUpload?> UploadMediaAsync(
        byte[] bytes, string contentType, string? filename = null, CancellationToken cancellationToken = default)
        => UploadAsync(Payloads.MessagingUploadMedia(Check(bytes), contentType, filename), cancellationToken);

    /// <summary>Upload a file from disk for WhatsApp. The type is guessed from the extension when omitted.</summary>
    public async Task<MessagingUpload?> UploadMediaAsync(
        string path, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await UploadMediaAsync(bytes, contentType ?? TypeOf(path), Path.GetFileName(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upload bytes for Telegram; send <c>result.UploadId</c> (<c>tgf_…</c>) as the media.</summary>
    public Task<MessagingUpload?> UploadTelegramFileAsync(
        byte[] bytes, string contentType, string? filename = null, CancellationToken cancellationToken = default)
        => UploadAsync(Payloads.MessagingUploadTelegram(Check(bytes), contentType, filename), cancellationToken);

    /// <summary>Upload a file from disk for Telegram. The type is guessed from the extension when omitted.</summary>
    public async Task<MessagingUpload?> UploadTelegramFileAsync(
        string path, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await UploadTelegramFileAsync(bytes, contentType ?? TypeOf(path), Path.GetFileName(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The send body in the API's field names.</summary>
    internal static JsonObject Body(SendMessage m)
    {
        if (m is null)
        {
            throw new ArgumentNullException(nameof(m));
        }

        Guard.NotBlank(m.Channel, nameof(m.Channel));
        Guard.NotBlank(m.To, nameof(m.To));
        if (m.Body is null && m.Content is null && m.Template is null)
        {
            throw new ArgumentException("A message needs Body, Content or Template.", nameof(m));
        }

        JsonObject? variables = null;
        if (m.Variables is not null)
        {
            variables = new JsonObject();
            foreach (var pair in m.Variables)
            {
                variables[pair.Key] = pair.Value;
            }
        }

        return JsonBody.Create()
            .Set("channel", m.Channel)
            .Set("to", m.To)
            .Set("body", m.Body)
            .SetNode("content", m.Content is null ? null : JsonHelpers.ToObjectNode(m.Content))
            .Set("sender", m.Sender)
            .Set("templateKey", m.Template)
            .Set("templateLocale", m.Language)
            .SetNode("templateVariables", variables)
            .Set("messageClass", m.MessageClass)
            .Set("scheduledAt", m.ScheduledAt?.ToString("o", CultureInfo.InvariantCulture))
            .Set("validityPeriodSec", m.ValidityPeriodSec)
            .SetNode("metadata", m.Metadata is null ? null : JsonHelpers.ToObjectNode(m.Metadata))
            .Set("trackLinks", m.TrackLinks)
            .Set("clientIp", m.ClientIp)
            .Build();
    }

    internal static string NewIdempotencyKey() => "nxs-" + Guid.NewGuid().ToString("N");

    private async Task<MessagingUpload?> UploadAsync(NexusRequest request, CancellationToken cancellationToken)
        => await Client.ExecuteMessagingAsync(request, cancellationToken).ConfigureAwait(false) is JsonObject o
            ? new MessagingUpload(o)
            : null;

    private static byte[] Check(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            throw new ArgumentException("The file is empty.", nameof(bytes));
        }

        return bytes;
    }

    private static string TypeOf(string path)
        => Types.TryGetValue(Path.GetExtension(path), out var type)
            ? type
            : throw new ArgumentException("Pass contentType (e.g. \"image/jpeg\"); it could not be guessed for " + path + ".", nameof(path));

    private static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Guard.NotBlank(path, nameof(path));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

/// <summary>One-time codes over Telegram, with SMS fallback.</summary>
public sealed class VerificationsResource : NexusResource
{
    internal VerificationsResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Send a code. Keep the returned <see cref="Verification.Id"/> to check it.</summary>
    public async Task<Verification?> StartAsync(StartVerification verification, CancellationToken cancellationToken = default)
    {
        if (verification is null)
        {
            throw new ArgumentNullException(nameof(verification));
        }

        Guard.NotBlank(verification.To, nameof(verification.To));
        var body = JsonBody.Create()
            .Set("to", verification.To)
            .Set("codeLength", verification.CodeLength)
            .Set("ttl", verification.Ttl)
            .Set("fallback", verification.SmsFallback ? "sms" : null)
            .Set("fallbackAfter", verification.FallbackAfter)
            .Set("smsSender", verification.SmsSender)
            .Set("smsTemplate", verification.SmsTemplate)
            .Set("senderUsername", verification.SenderUsername)
            .Set("maxAttempts", verification.MaxAttempts)
            .SetNode("metadata", verification.Metadata is null ? null : JsonHelpers.ToObjectNode(verification.Metadata))
            .Build();
        return Read(await Client.ExecuteMessagingAsync(Payloads.VerificationStart(body), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Check what the user typed: <see cref="Verification.Valid"/> says whether it matched.</summary>
    public async Task<Verification?> CheckAsync(string id, string code, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(id, nameof(id));
        Guard.NotBlank(code, nameof(code));
        return Read(await Client.ExecuteMessagingAsync(Payloads.VerificationCheck(id, code), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A verification's current state.</summary>
    public async Task<Verification?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(id, nameof(id));
        return Read(await Client.ExecuteMessagingAsync(Payloads.VerificationGet(id), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Cancel a pending verification.</summary>
    public async Task<Verification?> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(id, nameof(id));
        return Read(await Client.ExecuteMessagingAsync(Payloads.VerificationCancel(id), cancellationToken).ConfigureAwait(false));
    }

    private static Verification? Read(JsonNode? node) => node is JsonObject o ? new Verification(o) : null;
}
