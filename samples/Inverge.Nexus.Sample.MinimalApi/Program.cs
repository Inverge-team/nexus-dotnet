// A minimal ASP.NET Core app wired to Nexus end to end.
//
// Run it with a real key:
//   NEXUS_API_KEY=nxs_live_xxx dotnet run
//
// Nothing here passes identity to Nexus by hand. UseNexus() binds the signed-in
// user, the route and the request id once per request, and every capture
// underneath inherits them.

using Inverge.Nexus;
using Inverge.Nexus.AspNetCore;
using Inverge.Nexus.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Reads the "Nexus" configuration section, then NEXUS_* from the environment,
// then this callback — so a container can repoint a deploy without a rebuild.
builder.Services.AddNexus(options =>
{
    options.Release = "sample@1.0.0";
    options.OsType = "server";
});

// Forward every existing ILogger call to the Nexus logs product. Records carrying
// an exception also become grouped issues in error monitoring.
builder.Logging.AddNexus(options => options.MinimumLevel = LogLevel.Information);

builder.Services.AddNexusAspNetCore(options =>
{
    // The sample has no authentication, so identity comes from a header.
    options.ResolveDistinctId = http =>
    {
        var header = http.Request.Headers["X-Demo-User"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header;
    };
});

var app = builder.Build();

app.UseNexus();

app.MapGet("/", () => Results.Ok(new { ok = true, sdk = NexusVersion.Current }));

// Buffered: this returns in microseconds and never fails the request.
app.MapPost("/orders", (INexusClient nexus, OrderRequest order) =>
{
    nexus.Track("order_placed", new { total = order.Total, currency = order.Currency });
    return Results.Accepted($"/orders/{order.Id}", order);
});

// One evaluation resolves every flag for this person; ask the result rather than
// calling IsEnabledAsync once per flag.
app.MapGet("/checkout", async (INexusClient nexus) =>
{
    var flags = await nexus.Flags.EvaluateAsync();
    return Results.Ok(new
    {
        newCheckout = flags.IsEnabled("new_checkout"),
        paywall = flags.Variant("paywall"),
    });
});

// Configuration that changes without a deploy. Defaults are what keep a config
// outage from changing behaviour.
app.MapGet("/config", async (INexusClient nexus) =>
{
    nexus.RemoteConfig.SetDefaults(new Dictionary<string, object?>
    {
        ["support_number"] = "+9647000000000",
        ["delivery_fee"] = 2.5,
        ["maintenance"] = false,
    });

    var config = await nexus.RemoteConfig.FetchAsync();
    return Results.Ok(new
    {
        supportNumber = config.GetString("support_number"),
        deliveryFee = config.GetNumber("delivery_fee"),
        maintenance = config.GetBoolean("maintenance"),
    });
});

// Rooms are environment-scoped: a subscriber on another environment's key is not
// in this room, and Recipients is how that shows up.
app.MapPost("/orders/{id}/ship", async (INexusClient nexus, string id) =>
{
    var ack = await nexus.EmitAsync($"orders:{id}", "status", new { state = "shipped" });
    return Results.Ok(new { ack.Recipients });
});

// The middleware reports this to error monitoring and rethrows it unchanged.
app.MapGet("/boom", void () => throw new InvalidOperationException("Deliberate failure."));

app.Run();

internal sealed record OrderRequest(string Id, decimal Total, string Currency);
