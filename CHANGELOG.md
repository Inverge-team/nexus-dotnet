# Changelog

All notable changes to the Nexus .NET SDK are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/) and the project uses
[semantic versioning](https://semver.org/).

## [1.0.0] — 2026-10-04

First release. Full coverage of the Nexus partner API from .NET.

### Added

- **`Inverge.Nexus`** — `NexusClient` / `INexusClient` covering sessions, events,
  logs, errors, feature flags, Remote Config, realtime rooms, deep links, surveys,
  push, in-app messages, Live Activities, session replay, and Voice
  (sessions/legs/IVR/transfer/conference/presence/quality/recordings). No
  third-party dependencies on net8.0 or net9.0.
- **Target frameworks** — net8.0, net9.0 and netstandard2.0, so .NET Framework
  4.6.2+ deployments are reachable.
- **Ambient context** — `NexusContext` on `AsyncLocal<T>`: bind identity, session,
  device and scope properties once at the edge of a request and everything captured
  underneath inherits them. Resolution order is options → ambient → per-call.
- **Buffered ingest** — events and log lines batch on a background loop, grouped by
  identity, with a bounded queue that drops and counts rather than growing memory,
  and a flush on dispose.
- **`Inverge.Nexus.Extensions.Logging`** — `services.AddNexus()` bound to
  `IConfiguration` and `IHttpClientFactory`, a `NexusLoggerProvider` that forwards
  `ILogger` records to Nexus logs (and records carrying an exception to error
  monitoring), the SDK's own diagnostics routed to `ILogger`, and an
  `IHostedService` that flushes on shutdown.
- **`Inverge.Nexus.AspNetCore`** — middleware that binds the request's identity,
  route, device and country, and reports unhandled exceptions before rethrowing
  them unchanged.
- **`Inverge.Nexus.Realtime`** — a Socket.IO listener with automatic re-join after
  a reconnect, since server-side room membership does not survive one.
- **Typed errors** — `NexusException` and friends carrying status, code and
  details, including a dedicated `NexusBillingSuspendedException` for the 402 the
  data plane returns while an organisation's invoices are unpaid.
- **Retries** — exponential backoff with jitter and `Retry-After`, applied per
  endpoint rather than per verb: ingest and reads are retried; push sends, voice
  legs, realtime emits and survey responses never are.
- **Fluent builders** — `Push.Notification()` and `LiveActivities.Activity()`, both
  refusing to send without a title or an audience.
- **Typed results with the raw payload attached**, so a field the server adds does
  not need an SDK upgrade.
- Trim- and native-AOT-compatible core, with the one reflection path (serializing a
  caller's POCO) documented and avoidable.
- XML documentation on every public member; 119 tests.

### Notes

- The `nxs_…` API key format and the `/partner/*` surface match the Python, PHP,
  TypeScript and Flutter SDKs.
- `Inverge.Nexus.Realtime` pins `SocketIOClient` to the 3.1 line. Version 4 depends
  on `Microsoft.Extensions.Logging` 10.x, which would push out-of-band 10.x
  assemblies onto every .NET 8 consumer of an optional package.
