# Nexus .NET SDK

The official .NET client for the **Inverge Nexus** platform: product analytics,
structured logging, error monitoring, sessions, feature flags, Remote Config,
realtime rooms, deep-link attribution, surveys, push notifications, in-app
messages, Live Activities, session replay, and **Voice (CPaaS)** — all correlated
to a single user journey.

- **`Inverge.Nexus`** — the client. No third-party dependencies.
- Targets **net8.0**, **net9.0** and **netstandard2.0** (so .NET Framework 4.6.2+
  is reachable too).
- Async throughout, `CancellationToken` everywhere, trim- and AOT-friendly.
- First-class **ASP.NET Core**, **`ILogger`** and **Generic Host** integration.

---

## Contents

1. [Install](#1-install)
2. [Quick start](#2-quick-start)
3. [The packages](#3-the-packages)
4. [Configuration](#4-configuration)
5. [Identity and ambient context](#5-identity-and-ambient-context)
6. [Sessions](#6-sessions)
7. [Events](#7-events)
8. [Logs](#8-logs)
9. [Errors](#9-errors)
10. [Feature flags](#10-feature-flags)
11. [Remote Config](#11-remote-config)
12. [Realtime](#12-realtime)
13. [Deep links and attribution](#13-deep-links-and-attribution)
14. [Surveys](#14-surveys)
15. [Push notifications](#15-push-notifications)
16. [In-app messages](#16-in-app-messages)
17. [Live Activities](#17-live-activities)
18. [Session replay](#18-session-replay)
19. [Voice (CPaaS)](#19-voice-cpaas)
20. [ASP.NET Core](#20-aspnet-core)
21. [Batching, flushing and shutdown](#21-batching-flushing-and-shutdown)
22. [Errors, retries and billing suspension](#22-errors-retries-and-billing-suspension)
23. [Transports and dispatchers](#23-transports-and-dispatchers)
24. [Trimming and native AOT](#24-trimming-and-native-aot)
25. [Testing against the SDK](#25-testing-against-the-sdk)
26. [Low-level requests](#26-low-level-requests)

---

## 1. Install

```bash
dotnet add package Inverge.Nexus
```

Optional, depending on what you are building:

```bash
dotnet add package Inverge.Nexus.Extensions.Logging   # DI, ILogger bridge, flush on shutdown
dotnet add package Inverge.Nexus.AspNetCore           # request middleware
dotnet add package Inverge.Nexus.Realtime             # listen to realtime rooms
```

An API key belongs to **one environment**, and everything a client writes lands in
that environment. Use a separate key per environment.

---

## 2. Quick start

### A console app or worker

```csharp
using Inverge.Nexus;

await using var nexus = new NexusClient("nxs_live_xxx");   // or set NEXUS_API_KEY

using (NexusContext.Scope(distinctId: "user_123"))
{
    await nexus.IdentifyAsync("user_123", email: "a@b.com", traits: new { plan = "pro" });

    nexus.Track("order_placed", new { total = 42, currency = "USD" });
    nexus.Logs.Information("Order accepted", source: "checkout");
}
```

`await using` matters: it flushes buffered telemetry on the way out. A process that
exits without flushing loses whatever was still buffered.

### ASP.NET Core

```csharp
using Inverge.Nexus;
using Inverge.Nexus.AspNetCore;
using Inverge.Nexus.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNexus();                 // reads "Nexus" config + NEXUS_* env
builder.Logging.AddNexus();                  // forward ILogger to Nexus logs
builder.Services.AddNexusAspNetCore();

var app = builder.Build();

app.UseAuthentication();
app.UseNexus();                              // after auth, so the user is bound

app.MapPost("/orders", (INexusClient nexus, Order order) =>
{
    // No identity passed: the middleware already bound the signed-in user,
    // the route and the request id for this request.
    nexus.Track("order_placed", new { total = order.Total });
    return Results.Accepted();
});

app.Run();
```

Working samples live in [`samples/`](https://github.com/Inverge-team/nexus-dotnet/tree/dev/samples).

---

## 3. The packages

| Package | What it adds | Targets |
|---|---|---|
| `Inverge.Nexus` | The client and every product. No third-party dependencies. | net8.0, net9.0, netstandard2.0 |
| `Inverge.Nexus.Extensions.Logging` | `AddNexus()` DI registration bound to `IConfiguration` and `IHttpClientFactory`, an `ILogger` provider, and a hosted service that flushes on shutdown. | net8.0, net9.0, netstandard2.0 |
| `Inverge.Nexus.AspNetCore` | Middleware that binds the request's identity, route and device, and reports unhandled exceptions. | net8.0, net9.0 |
| `Inverge.Nexus.Realtime` | A Socket.IO connection for *receiving* room traffic. | net8.0, net9.0, netstandard2.0 |

The split keeps the core dependency-free: a console app that only sends events
does not pull in ASP.NET Core or a Socket.IO client.

---

## 4. Configuration

```csharp
var nexus = new NexusClient("nxs_live_xxx", options =>
{
    options.BaseUrl = "https://services.inverge.net";
    options.Release = "api@2026.10.4";
    options.AppVersion = "2.1.0";
    options.OsType = "server";

    options.Batch = true;                               // buffer events and logs
    options.FlushInterval = TimeSpan.FromSeconds(5);
    options.MaxBatch = 50;
    options.MaxQueue = 10_000;

    options.Timeout = TimeSpan.FromSeconds(10);
    options.MaxRetries = 2;

    options.DefaultProperties["tier"] = "enterprise";    // on every event
    options.DefaultHeaders["X-Tenant"] = "acme";         // on every request
});
```

### Precedence

Lowest to highest: **`appsettings`** → **`NEXUS_*` environment** → **code**.

That ordering is the point. It lets `NEXUS_BASE_URL` repoint a staging deploy whose
API key is assigned in code. Without it, passing a key in code would silently send
staging traffic to production.

### Environment variables

`NEXUS_API_KEY`, `NEXUS_BASE_URL`, `NEXUS_REALTIME_URL`, `NEXUS_TIMEOUT`,
`NEXUS_MAX_RETRIES`, `NEXUS_APP_VERSION`, `NEXUS_RELEASE`, `NEXUS_BATCH`,
`NEXUS_FLUSH_INTERVAL`, `NEXUS_MAX_BATCH`, `NEXUS_MAX_QUEUE`, `NEXUS_SILENT`,
`NEXUS_DEBUG`, `NEXUS_USER_AGENT`.

Durations are **seconds** (`NEXUS_TIMEOUT=2.5`).

### appsettings.json

```json
{
  "Nexus": {
    "ApiKey": "nxs_live_xxx",
    "BaseUrl": "https://services.inverge.net",
    "Timeout": 10,
    "MaxBatch": 50,
    "DefaultHeaders": { "X-Tenant": "acme" }
  }
}
```

`Timeout` and the other durations accept a number of seconds or the
`"00:00:10"` spelling. Prefer a configuration provider or a secret store over
putting a live key in a committed file.

---

## 5. Identity and ambient context

Every event, log line and error carries identity, a session and device fields.
Rather than threading those through every call, bind them once:

```csharp
using (NexusContext.Scope(distinctId: "user_123", url: "/checkout", country: "IQ"))
{
    nexus.Track("checkout_started");          // carries user_123
    nexus.Logs.Information("Reserving stock"); // and so does this
}
```

`NexusContext` is backed by `AsyncLocal<T>`, so it is correct per request and flows
across `await` into whatever the handler calls, including thread-pool work. Nested
scopes inherit what they do not set.

One `AsyncLocal` caveat: a value set *inside* an `async` method does not flow back
out to its caller. Bind at the level that owns the context — the middleware, the job
handler — rather than deep inside a helper.

Resolution order for any field, lowest first:

1. `NexusOptions` (`Release`, `AppVersion`, `OsType`, `OsVersion`)
2. the ambient `NexusContext`
3. the `context:` argument on the call

```csharp
nexus.Track("order_placed", new { total = 42 },
    context: new NexusTelemetryContext { DistinctId = "user_999" });
```

On logout:

```csharp
nexus.Reset();   // forgets the person, keeps the device, starts a new session
```

---

## 6. Sessions

Events, logs, errors, surveys and replay all correlate into a session, which is what
turns scattered telemetry into one person's story.

```csharp
await nexus.Sessions.IdentifyAsync("user_123",
    email: "a@b.com", name: "Ada", phone: "+964700", traits: new { plan = "pro" });

var session = await nexus.Sessions.TrackAsync();   // start or refresh
Console.WriteLine(session.SessionId);
```

`nexus.IdentifyAsync(...)` does both at once: binds the person locally *and* upserts
them server-side.

---

## 7. Events

```csharp
nexus.Track("order_placed", new { total = 42, currency = "USD" });
nexus.Events.Track("signup", new Dictionary<string, object?> { ["plan"] = "pro" });
```

`Track` is buffered and returns in microseconds — it costs nothing on a request's
hot path and cannot fail it. Properties layer: client `DefaultProperties`, then the
ambient scope's properties, then the call's own.

To send immediately, or to replay events that already happened:

```csharp
var written = await nexus.Events.BatchAsync(new[]
{
    new NexusEvent("page_view", new { path = "/" }, DateTimeOffset.UtcNow.AddMinutes(-5)),
    new NexusEvent("page_view", new { path = "/pricing" }),
});
```

Omit the timestamp and the server stamps arrival time — which is wrong for a replay.
At most **1000** events per batch; the SDK checks before sending.

---

## 8. Logs

```csharp
nexus.Logs.Information("Payment started", source: "checkout", logContext: new { orderId = 42 });
nexus.Logs.Warning("Retrying gateway");
nexus.Logs.Error("Gateway unreachable");
```

Buffered like events. If the app already uses `ILogger`, prefer the bridge in
§[20](#20-aspnet-core) — it forwards every existing call with no edits.

---

## 9. Errors

```csharp
try
{
    await ChargeAsync(order);
}
catch (PaymentException exception)
{
    await nexus.Errors.CaptureExceptionAsync(exception,
        errorContext: new { orderId = order.Id });
}
```

Frames come back **throw-site first**, so the first line in the console is where it
broke, and inner exceptions are appended behind a `caused by …` marker — usually
where the real cause is.

Three outcomes all arrive as a success, and they are not the same thing:

```csharp
var result = await nexus.Errors.CaptureAsync("Gateway timeout", type: "GatewayTimeout");

result.Stored    // saved, and billed
result.Ignored   // the issue is marked ignored in the console: dropped, not billed
result.Skipped   // the environment hit its error limit: dropped
```

When a message varies per request — an order id, a user name — grouping splits one
real problem into hundreds of issues. Pin it:

```csharp
await nexus.Errors.CaptureAsync($"Gateway timeout for order {order.Id}",
    fingerprint: "gateway-timeout");

// Or derive a stable key from the throw site:
await nexus.Errors.CaptureExceptionAsync(exception,
    fingerprint: ErrorsResource.Fingerprint(exception));
```

File and line numbers need the PDB next to the assembly. Without one the frames
still carry method names, which is enough to group and to symbolicate later.

---

## 10. Feature flags

One evaluation resolves **every** flag in the environment, so ask the result rather
than asking the server again:

```csharp
var flags = await nexus.Flags.EvaluateAsync("user_1", new { plan = "pro" });

if (flags.IsEnabled("new_checkout")) { … }
var variant = flags.Variant("paywall");       // multivariate
var copy = flags.Payload("paywall");          // attached JSON
```

Three `IsEnabledAsync` calls are three round trips for data the first one already had.

The convenience helpers **degrade instead of throwing**:

```csharp
// Returns the fallback and reports the failure if Nexus is unreachable.
var enabled = await nexus.IsEnabledAsync("new_checkout", "user_1", fallback: false);
```

A flag lookup is a decision point, not a data fetch: letting an outage turn into a
500 on a page that would otherwise render is the wrong trade. `EvaluateAsync` still
throws, so you can handle it explicitly where it matters.

An empty evaluation means the environment hit its evaluation limit — not that every
flag is off — so the helpers fall back there too.

---

## 11. Remote Config

Change behaviour without a deploy: a fee, a phone number, a copy string, a kill
switch. Conditions (platform, version, country, percentile, custom attributes) are
evaluated server-side, so what arrives is final.

```csharp
nexus.RemoteConfig.SetDefaults(new Dictionary<string, object?>
{
    ["delivery_fee"] = 2.5,
    ["support_number"] = "+9647000000000",
    ["maintenance"] = false,
});

var config = await nexus.RemoteConfig.FetchAsync();

config.GetNumber("delivery_fee");
config.GetString("support_number");
config.GetBoolean("maintenance");
config.SourceOf("delivery_fee");     // which condition supplied it, or null
```

Register defaults at startup. They are what keeps a config outage from changing
behaviour — without one, a missing key reads as `0`, `false` or `""`.

Reads are cached in-process for `RemoteConfigCacheTtl` (30s by default) and
revalidated with the previous ETag, so a hot path can read freely. `config.Throttled`
tells you the environment hit its fetch limit and you are being served the last good
values plus your defaults.

Writing edits a **draft**; nothing changes for anyone until you publish:

```csharp
await nexus.RemoteConfig.SetParameterAsync("delivery_fee", 3.0, description: "Fuel");
var version = await nexus.RemoteConfig.PublishAsync("Raise delivery fee");

// Or both at once:
await nexus.RemoteConfig.SetAsync("maintenance", true);
```

Resolving config **on behalf of a device** needs the real platform, or platform
conditions will not match:

```csharp
await nexus.RemoteConfig.FetchAsync(new Dictionary<string, object?>
{
    ["platform"] = "ios",          // defaults to "server"
    ["appVersion"] = "3.4.1",
    ["country"] = "IQ",
    ["userProperties"] = new { governorate = "Basra" },
});
```

---

## 12. Realtime

```csharp
var ack = await nexus.EmitAsync("orders:42", "status", new { state = "shipped" });
Console.WriteLine(ack.Recipients);
```

Rooms are **environment-scoped**. A subscriber connected with a key from another
environment is not in the room you emitted to, and the emit succeeds with
**zero recipients**. That silence looks exactly like a bug in your handler, so
`Recipients` is worth checking when messages "just don't arrive".

```csharp
// Many rooms, one request:
await nexus.Realtime.BroadcastAsync(new[]
{
    new RoomMessage("orders:1", "location", new { lat = 1 }),
    new RoomMessage("orders:2", new[] { "location", "eta" }, new { lat = 2 }),
});

// The room registry and payload schemas:
var room = await nexus.Realtime.RegisterRoomAsync("orders:42", type: "order");
await nexus.Realtime.SetSchemaAsync(room.Id!, schema);
await nexus.Realtime.EnableSchemaAsync(room.Id!);     // attaching ≠ enforcing
await nexus.Realtime.LinkAsync(room.Id!, otherRoomId); // ids, not names
```

### Listening

The core package only emits. To receive, add `Inverge.Nexus.Realtime`:

```csharp
using Inverge.Nexus.Realtime;

await using var socket = new NexusRealtimeSocket(nexus);

socket.On("status", message => Console.WriteLine(message.Payload));
socket.Error += (_, error) =>
{
    if (error.IsBillingSuspended) { /* an invoice is unpaid */ }
};

await socket.ConnectAsync();
await socket.JoinAsync("orders:42");
```

Handlers registered before connecting are attached on connect, and joined rooms are
**re-joined after every reconnect** — server-side membership does not survive one, so
without that a reconnected socket goes quiet permanently.

If the server refuses the handshake with `billing_suspended`, the socket stops
instead of reconnecting. Retrying cannot clear a suspension, and a reconnect loop
buries the one error that explains it. Set
`options.StopOnBillingSuspended = false` to opt out.

---

## 13. Deep links and attribution

```csharp
await nexus.Links.InstallAsync(clickId: "abc", name: "summer_sale", platform: "ios");
await nexus.Links.OpenAsync(clickId: "abc");
await nexus.Links.AttributeAsync("in_app", revenue: 12.5, properties: new { sku = "A1" });
```

Pass `clickId` whenever the client has it — without it, attribution falls back to a
probabilistic match.

---

## 14. Surveys

Headless: Nexus targets and stores, you render.

```csharp
var surveys = await nexus.Surveys.ActiveAsync(deviceType: "phone");

await nexus.Surveys.CompleteAsync("survey_1", new Dictionary<string, object?>
{
    ["q1"] = 9,
    ["q2"] = "Fast delivery",
});

await nexus.Surveys.DismissAsync("survey_1");
```

Send the dismissal. It feeds frequency capping, so skipping it means the same survey
keeps being offered to someone who already said no.

Pass a previous response's `Id` back as `responseId` to append to it, instead of
creating a second response per partial submission.

---

## 15. Push notifications

```csharp
var result = await nexus.Push.Notification()
    .Title("Your order shipped")
    .Body("Track it in the app")
    .Image("https://cdn.example.com/box.png")
    .Data(new { screen = "/orders/42" })
    .Button("track", "Track", url: "https://example.com/track")
    .IosBadge(1)
    .IosInterruptionLevel("time-sensitive")
    .AndroidVisibility("public")
    .ToSegments(new[] { "vip", "active_7d" })
    .SendAsync();

result.Sent; result.Failed; result.Recipients;
```

Or directly:

```csharp
await nexus.Push.SendToUserAsync("user_1", "Welcome 👋", body: "Glad you're here");
```

A send with no audience is refused rather than quietly meaning *everyone*; reaching
everyone is spelled `ToEveryone()`. A steady non-zero `Failed` usually means stale
tokens or provider credentials that need attention — the send still reports success.

Device tokens, for server-driven flows (the device SDKs normally do this):

```csharp
await nexus.Push.RegisterTokenAsync(token, "ios", tags: new Dictionary<string, string> { ["city"] = "Basra" });
await nexus.Push.UnregisterAsync(token);          // logout, uninstall
await nexus.Push.OpenedAsync(campaignId, token);  // delivery analytics
```

---

## 16. In-app messages

```csharp
var messages = await nexus.InApp.ActiveAsync();

await nexus.InApp.ImpressionAsync("msg_1");
await nexus.InApp.ClickAsync("msg_1", "cta");
await nexus.InApp.DismissAsync("msg_1");
```

---

## 17. Live Activities

```csharp
await nexus.LiveActivities.Activity("DeliveryAttributes", "order_42")
    .Title("Order #42").Status("Preparing").Progress(20)
    .ToUser("user_1").Priority(10)
    .StartAsync();

await nexus.LiveActivities.Activity("order_42").Status("On the way").Progress(70).UpdateAsync();
await nexus.LiveActivities.Activity("order_42").Status("Delivered").EndAsync();
```

`Activity(type, id)` starts one; `Activity(id)` updates or ends it. Priority 5 is
routine and unmetered; 10 is immediate and metered. `Attributes(...)` are fixed at
start — on iOS they are ActivityKit's static attributes and a later update cannot
change them, so anything that will change belongs in the content state.

iOS needs the APNs key configured in the console (Live Activities cannot go through
FCM) and a Widget Extension in the app. Android needs no extra setup.

---

## 18. Session replay

Replay is normally recorded by the browser and mobile SDKs. This resource is for
proxying those chunks through your own backend, or replaying synthetic sessions.

```csharp
await nexus.Replay.IngestAsync("rec_42", events, href: "/checkout", width: 1440, height: 900);
```

Keep `recordingId` stable across every chunk — a new id starts a new recording and
splits one session into several. At most **5000** events per chunk.

---

## 19. Voice (CPaaS)

A call is a **session** with one or more **legs**. The tenant is always the
environment behind your API key, and grants, rooms and caller ID are decided
server-side.

```csharp
var call = await nexus.Voice.CreateCallAsync(direction: "outbound", recordingMode: "mixed");

var agent = await nexus.Voice.AddLegAsync(call.SessionId!, "agent", "webrtc");
agent.Join!.Access!.Token;   // hand this and Join.Turn to the device SDK

var callee = await nexus.Voice.AddLegAsync(
    call.SessionId!, "callee", "pstn", address: "+9647001234567", callerId: "+9647009999999");

await nexus.Voice.RingAsync(callee.Leg!.Id!);
await nexus.Voice.AnswerAsync(callee.Leg.Id!);
await nexus.Voice.HangupAsync(callee.Leg.Id!, reason: "completed");
```

> **The one thing to know.** `AddLegAsync` reports failure *inside a successful
> response*. A blocked call, a missing carrier and an unreachable media plane all
> arrive as 2xx with an error code, so that a client can end the call cleanly
> instead of treating a provisioning problem as a crash.

```csharp
var leg = await nexus.Voice.AddLegAsync(sessionId, "callee", "pstn", address: number);

if (!leg.IsSuccess)
{
    if (leg.IsBlocked)             { /* fraud policy refused it; leg.Reason says why */ }
    if (leg.HasNoOutboundTrunk)    { /* no PSTN carrier configured */ }
    if (leg.IsMediaUnavailable)    { /* media plane unconfigured or unreachable */ }
}

leg.EnsureSuccess();   // or turn it into an exception
```

Hangup reasons matter: they are what distinguish a missed call from a declined one
in the console.

### IVR, transfer, conference

```csharp
var ivr = await nexus.Voice.StartIvrAsync("flow_main");
var next = await nexus.Voice.SendIvrInputAsync(ivr.SessionId!, "2");

if (next.Instruction!.IsConnect)      { /* call Instruction.To over Instruction.EndpointType */ }
if (next.Instruction.HasNoAgents)     { /* the queue resolved to nobody available */ }

// A keypress during a call carries the next IVR step with it:
var dtmf = await nexus.Voice.SendDigitAsync(sessionId, legId, "1");
dtmf.Ivr?.Action;

await nexus.Voice.TransferBlindAsync(sessionId, legId, "app", targetIdentityId: "agent_2");
await nexus.Voice.TransferWarmStartAsync(sessionId, otherLegId, "app", targetIdentityId: "agent_2");
await nexus.Voice.TransferWarmCompleteAsync(otherLegId, consultLegId);

await nexus.Voice.MuteAsync(legId);
await nexus.Voice.RemoveParticipantAsync(sessionId, legId);
```

Finish a warm transfer. Leaving one unfinished leaves the other party on hold.

### Devices, presence, quality, recordings

```csharp
await nexus.Voice.RegisterDeviceAsync("user_123", deviceId, "android", fcmToken: token);
await nexus.Voice.PresenceAsync("user_123", deviceId, "ONLINE");
await nexus.Voice.ReportQualityAsync(sessionId, legId, rttMs: 48, mos: 4.1, candidateType: "relay");

var url = nexus.Voice.RecordingDownloadUrl(accessToken);
```

Calls ring an **identity**, so after a login the device must be re-registered under
the logged-in identity or calls ring an identity nobody is listening on.

Recordings have no permanent URL: the console mints a short-lived token bound to one
recording. Treat the resulting link as a secret with a short life — do not store it,
and keep it out of logs.

---

## 20. ASP.NET Core

### Registration

```csharp
builder.Services.AddNexus(options => options.Release = "api@2026.10.4");
builder.Services.AddNexusAspNetCore();
builder.Logging.AddNexus();
```

`AddNexus()` registers `INexusClient` as a singleton, sends through a named
`IHttpClientFactory` client (so your handlers, resilience policies and connection
lifetime apply), routes the SDK's own diagnostics to `ILogger`, and adds a hosted
service that flushes buffered telemetry on shutdown.

### Middleware

```csharp
app.UseAuthentication();
app.UseNexus();           // after auth, so the signed-in user is available
```

It binds the person, session, device, country, route and request id into the ambient
context, and reports unhandled exceptions before rethrowing them unchanged — your own
error handling still runs.

```csharp
builder.Services.AddNexusAspNetCore(options =>
{
    options.ResolveDistinctId = http => http.User.FindFirst("tenant_user")?.Value;
    options.IgnoredPathPrefixes.Add("/internal");
    options.TrackRequests = true;        // off by default
    options.IncludeQueryString = true;   // off by default
});
```

Two defaults are deliberate. **Query strings are excluded**, because they routinely
carry tokens and invite codes and telemetry is the last place those should be copied
to. **Per-request events are off**, because events are metered and a busy service
would bill one for every request, health checks included.

Health, readiness and metrics paths are ignored out of the box.

### The ILogger bridge

```csharp
builder.Logging.AddNexus(options =>
{
    options.MinimumLevel = LogLevel.Information;   // a second gate on top of filters
    options.CaptureExceptions = true;              // records with an exception → issues
    options.IncludeScopes = true;                  // request id and route into context
});
```

Every existing `logger.LogInformation("Charging {OrderId}", id)` now lands in Nexus
with the template's arguments still queryable, and `logger.LogError(ex, …)` also
produces a grouped, stack-traced issue.

The bridge drops two kinds of record so it cannot feed on itself: anything in the
`Inverge.Nexus` category, and anything written *while the SDK is delivering*.
`HttpClient` logs every request at `Information`, so without the second guard a
globally registered bridge would log its own traffic, whose delivery logs again,
without end.

Writing a bridge for another logging framework? Check
`Inverge.Nexus.Diagnostics.NexusDelivery.IsDelivering` and do the same.

---

## 21. Batching, flushing and shutdown

Events and log lines are buffered and shipped on a background loop, grouped by
identity so each request carries one person's items — which is the shape the ingest
endpoints want.

The buffer is **bounded** (`MaxQueue`). Under a flood, or while the backend is
unreachable, the newest items are dropped and counted rather than growing memory
until the process dies:

```csharp
nexus.Pending   // waiting to be sent
nexus.Dropped   // lost to a full buffer — worth exposing as a metric
```

Flushing:

```csharp
await nexus.FlushAsync();                          // ship everything now
await nexus.FlushAsync(TimeSpan.FromSeconds(5));   // with a deadline
```

`await using` (or `DisposeAsync`) flushes on the way out. In a hosted app the
registered hosted service does it inside the host's shutdown timeout, which runs
earlier than container disposal.

Delivery failures never reach the caller. `Track` returns `void`; a telemetry
outage is not the application's problem, so failures go to diagnostics instead:

```csharp
options.DiagnosticSink = (level, message, exception) =>
    Console.Error.WriteLine($"[nexus:{level}] {message}");
```

`AddNexus()` wires that to `ILogger` for you.

---

## 22. Errors, retries and billing suspension

Every failure derives from `NexusException`, so one `catch` contains the SDK:

| Exception | When |
|---|---|
| `NexusConfigurationException` | No API key, a malformed origin |
| `NexusTransportException` | DNS, refused connection, TLS, timeout |
| `NexusValidationException` | 400 / 422 |
| `NexusAuthenticationException` | 401 — key missing, malformed or revoked |
| `NexusPermissionDeniedException` | 403 |
| `NexusNotFoundException` | 404 |
| `NexusBillingSuspendedException` | 402 — unpaid invoices |
| `NexusRateLimitException` | 429, with `RetryAfter` |
| `NexusServerException` | 5xx |

Match on `Code`, not on the message: codes are part of the contract.

```csharp
catch (NexusBillingSuspendedException)
{
    // The whole data plane is suspended until an invoice is paid. Retrying cannot
    // help, so the SDK never does — tell whoever can pay the bill.
}
```

**Retries are decided per endpoint, not per verb.** Telemetry ingest and reads are
retried with exponential backoff, jitter and `Retry-After`. A push send, a voice leg,
a realtime emit and a survey response are **never** retried, however `MaxRetries` is
set: repeating one after a timeout would double something a person can see. Client
errors — a 402 included — are not retried either, since the answer cannot change.

`options.Silent = true` makes direct calls return an empty result instead of
throwing, and reports the failure to diagnostics. Background delivery is always
silent.

---

## 23. Transports and dispatchers

By default every client in the process shares one pooled `HttpClient` whose
connections recycle every five minutes, so a long-lived process still follows DNS
changes. In a hosted app, `AddNexus()` hands the client an `IHttpClientFactory`
client instead.

```csharp
var nexus = new NexusClient(options, httpClient);        // your HttpClient, not disposed
var nexus = new NexusClient(options, myTransport);       // your INexusTransport
```

Implement `INexusTransport` to put the SDK's traffic through your own pipeline —
a proxy, a recorded fixture, a test double. Return the response for any status the
server produced, including 4xx and 5xx; throw `NexusTransportException` only when
there was no response at all.

Fire-and-forget everything:

```csharp
var background = nexus.Background();
await background.Push.SendToUserAsync("user_1", "Welcome");   // returns immediately
```

Calls that need a response cannot work through it — use the normal client for flags
and Remote Config.

---

## 24. Trimming and native AOT

`Inverge.Nexus`, `Inverge.Nexus.Extensions.Logging` and `Inverge.Nexus.AspNetCore`
are marked trimmable and AOT-compatible. Request bodies are assembled as explicit
`JsonObject` graphs and responses are read field by field, so the SDK's own paths use
no reflection.

The one reflection path is serializing **your** value as event properties, a payload
or a content state. That exists so `new { total = 42 }` works, which is the natural
C# spelling. In a trimmed or native-AOT app, pass an `IDictionary<string, object?>`
or a `JsonNode` instead:

```csharp
nexus.Track("order_placed", new Dictionary<string, object?> { ["total"] = 42 });
nexus.Track("order_placed", new JsonObject { ["total"] = 42 });
```

`Inverge.Nexus.Realtime` is not marked AOT-compatible: its Socket.IO dependency
serializes reflectively.

---

## 25. Testing against the SDK

Inject a transport and assert on what was sent:

```csharp
internal sealed class FakeTransport : INexusTransport
{
    public List<string> Paths { get; } = new();

    public Task<NexusTransportResponse> SendAsync(
        string method, string url, IReadOnlyDictionary<string, string> headers,
        byte[]? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Paths.Add(new Uri(url).AbsolutePath);
        return Task.FromResult(new NexusTransportResponse(
            200, Encoding.UTF8.GetBytes("""{"ok":true}""")));
    }

    public void Dispose() { }
}
```

```csharp
var transport = new FakeTransport();
await using var nexus = new NexusClient(
    new NexusOptions { ApiKey = "nxs_test", Batch = false }, transport);

await nexus.EmitAsync("orders:42", "status");

Assert.Equal("/partner/rooms/orders%3A42/emit", transport.Paths.Single());
```

Set `Batch = false` so a call sends inline and the test does not have to wait for a
background loop. The builders also expose `Build()`, so a payload can be asserted
without sending anything.

---

## 26. Low-level requests

For an endpoint newer than this SDK:

```csharp
var response = await nexus.SendAsync(new NexusRequest(
    "POST",
    "/partner/something/new",
    new JsonObject { ["key"] = "value" },
    retryable: false));
```

Headers, auth, retries, error mapping and diagnostics all apply as usual. Mark a
request `retryable` only when repeating it is genuinely harmless.

---

## Links

- Console — <https://nexus.inverge.net>
- Site — <https://inverge.net>
- Source — <https://github.com/Inverge-team/nexus-dotnet>

MIT licensed. © Inverge.
