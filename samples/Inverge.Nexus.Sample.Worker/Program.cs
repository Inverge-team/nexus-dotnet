// A plain console worker: no dependency injection, no host.
//
//   NEXUS_API_KEY=nxs_live_xxx dotnet run
//
// Two things matter in a short-lived process. Build one client and reuse it, and
// flush before exiting — telemetry that is still buffered when the process ends is
// gone, and that is exactly the window a crash-loop restart lands in.

using System.Text.Json.Nodes;
using Inverge.Nexus;
using Inverge.Nexus.Diagnostics;
using Inverge.Nexus.Realtime;

var apiKey = Environment.GetEnvironmentVariable("NEXUS_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set NEXUS_API_KEY to your nxs_... key first.");
    return 1;
}

// `await using` is what guarantees the shutdown flush.
await using var nexus = new NexusClient(apiKey, options =>
{
    options.Release = "worker@1.0.0";
    options.OsType = "server";

    // The core package has no logging dependency, so it reports on itself through
    // this callback. In a hosted app, AddNexus() wires it to ILogger for you.
    options.DiagnosticSink = (level, message, exception) =>
        Console.Error.WriteLine($"[nexus:{level}] {message}{(exception is null ? "" : " — " + exception.Message)}");
});

// Bind identity once; everything captured inside the scope inherits it.
using (NexusContext.Scope(distinctId: "user_123", sessionKey: NexusContext.NewSessionKey()))
{
    await nexus.IdentifyAsync("user_123", email: "a@b.com", traits: new { plan = "pro" });

    nexus.Track("job_started", new { job = "nightly-rollup" });
    nexus.Logs.Information("Rollup beginning", source: "rollup");

    try
    {
        await DoWorkAsync();
    }
    catch (Exception exception)
    {
        // Reported as unhandled, so the console surfaces it first.
        await nexus.CaptureExceptionAsync(exception, handled: false);
        throw;
    }

    nexus.Track("job_finished", new { job = "nightly-rollup", durationMs = 1234 });
}

// Listening needs a socket; the HTTP resource only emits.
await using var socket = new NexusRealtimeSocket(nexus, options =>
{
    options.DistinctId = "user_123";

    // A suspension is cleared by paying an invoice, not by retrying.
    options.StopOnBillingSuspended = true;
});

socket.Error += (_, error) =>
{
    if (error.IsBillingSuspended)
    {
        Console.Error.WriteLine("Nexus has suspended this organisation's data plane — an invoice is unpaid.");
    }
    else
    {
        Console.Error.WriteLine("Realtime error: " + error.Message);
    }
};

socket.On("status", message =>
{
    Console.WriteLine($"order status: {message.Payload?.ToJsonString() ?? "(none)"}");
});

try
{
    await socket.ConnectAsync();
    await socket.JoinAsync("orders:42");

    // Emitting from a server is better done over HTTP: no live connection needed,
    // and the call is reported and retried like any other.
    var ack = await nexus.EmitAsync("orders:42", "status", new JsonObject { ["state"] = "shipped" });
    Console.WriteLine($"delivered to {ack.Recipients} subscriber(s)");

    await Task.Delay(TimeSpan.FromSeconds(2));
}
catch (NexusException exception)
{
    Console.Error.WriteLine("Realtime unavailable: " + exception.Message);
}

// Explicit, though `await using` would do it too. In a process that calls
// Environment.Exit, this is the only thing that will.
await nexus.FlushAsync();
Console.WriteLine($"done — {nexus.Pending} pending, {nexus.Dropped} dropped");
return 0;

static Task DoWorkAsync() => Task.Delay(50);
