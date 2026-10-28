# Nexus .NET SDK

The official .NET client for the **Inverge Nexus** platform — every product,
correlated to a single user journey. Async throughout, `CancellationToken`
everywhere, trim- and AOT-friendly, with first-class **ASP.NET Core**,
**`ILogger`** and **Generic Host** integration.

| Package | Adds |
|---|---|
| `Inverge.Nexus` | The client and every product. No third-party dependencies. |
| `Inverge.Nexus.Extensions.Logging` | DI registration, an `ILogger` provider and flush on shutdown. |
| `Inverge.Nexus.AspNetCore` | Middleware that binds the request's identity, route and device and reports unhandled exceptions. |
| `Inverge.Nexus.Realtime` | A connection for receiving room traffic. |
| `Inverge.Nexus.Telescope` | Request-journey tracing built on `System.Diagnostics.Activity`. |

Targets **net8.0**, **net9.0** and **netstandard2.0**.

## Documentation

Installation, configuration, ASP.NET Core setup and the full API reference live
on the documentation site:

- **Guide** — <https://docs.inverge.net/sdks/dotnet>
- **API reference** — <https://docs.inverge.net/sdks/dotnet/reference>

## What it covers

- **[Identity & ambient context](https://docs.inverge.net/sdks/dotnet#ambient-context)** — bind the current user and journey once per request.
- **[Sessions](https://docs.inverge.net/sdks/dotnet/reference/sessions)** — journey sessions that tie server-side activity to a user.
- **[Events](https://docs.inverge.net/sdks/dotnet/reference/events)** — product analytics sent from your backend.
- **[Logs](https://docs.inverge.net/sdks/dotnet/reference/logs)** — structured, leveled logs, plus an `ILogger` bridge.
- **[Errors](https://docs.inverge.net/sdks/dotnet/reference/errors)** — exceptions with context, reported to Nexus Errors.
- **[Feature flags](https://docs.inverge.net/sdks/dotnet/reference/flags)** — evaluate flags per user on the server.
- **[Remote Config](https://docs.inverge.net/sdks/dotnet/reference/remote-config)** — read remotely managed values, with targeting.
- **[Realtime](https://docs.inverge.net/sdks/dotnet/reference/realtime)** — emit to rooms and listen to room traffic.
- **[Deep links & attribution](https://docs.inverge.net/sdks/dotnet/reference/links)** — create and resolve Nexus links.
- **[Surveys](https://docs.inverge.net/sdks/dotnet/reference/surveys)** — manage and read in-product surveys.
- **[Push notifications](https://docs.inverge.net/sdks/dotnet/reference/push)** — send push to users, devices and tags.
- **[In-app messages](https://docs.inverge.net/sdks/dotnet/reference/inapp)** — trigger in-app messages for users.
- **[Live Activities](https://docs.inverge.net/sdks/dotnet/reference/live-activities)** — start, update and end Live Activities from your server.
- **[Session replay](https://docs.inverge.net/sdks/dotnet/reference/replay)** — forward replay chunks recorded by the browser and mobile SDKs through your own backend.
- **[Voice (CPaaS)](https://docs.inverge.net/sdks/dotnet/reference/voice)** — place and control calls: WebRTC and PSTN legs, IVR, transfer, conference, recordings.
- **[Shield](https://docs.inverge.net/products/shield/verify)** — verify a request really comes from your app on a real device.
- **[Telescope](https://docs.inverge.net/products/telescope/dotnet)** — request-journey tracing for requests, jobs, EF Core / SqlClient
  queries and HttpClient calls, with N+1, duplicate and slow queries flagged.
- **[DocStore](https://docs.inverge.net/sdks/dotnet/reference/docstore)** — the document database: queries, transactions, history, change stream and webhook verification.

## Releasing

Publishing is automated: merging `dev` into `main` publishes the version declared in
`Directory.Build.props`. See [RELEASING.md](https://github.com/Inverge-team/nexus-dotnet/blob/dev/RELEASING.md).

## Links

- Console — <https://nexus.inverge.net>
- Site — <https://inverge.net>
- Source — <https://github.com/Inverge-team/nexus-dotnet>

MIT licensed. © Inverge.
