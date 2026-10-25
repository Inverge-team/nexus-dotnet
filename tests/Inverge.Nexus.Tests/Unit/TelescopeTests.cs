using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.AspNetCore;
using Inverge.Nexus.Telescope;
using Inverge.Nexus.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

// One ActivityListener sees the whole process, so these run on their own.
[Collection("Telescope")]
public class TelescopeTests
{
    private static List<JsonObject> Batches(RecordingTransport transport)
        => transport.All()
            .Where(r => r.Path == "/partner/telescope")
            .SelectMany(r => r.Object["batches"]!.AsArray().Select(b => b!.AsObject()))
            .ToList();

    [Fact]
    public async Task A_job_records_spans_queries_and_logs_and_joins_the_producers_trace()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;
        using var telescope = new NexusTelescope(client, new TelescopeOptions { Service = "worker" });
        telescope.Start();

        const string producer = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        await telescope.RunAsync("job", "SendInvoice", async () =>
        {
            using (telescope.Span("render pdf"))
            {
                await Task.Delay(5);
                telescope.RecordQuery("select * from invoices where id = @id", new object?[] { 7 }, 1.2, "postgres");
            }

            telescope.Log("warning", "slow renderer", new Dictionary<string, object?> { ["password"] = "x" });
        }, producer);
        await telescope.FlushAsync();

        var job = Batches(transport).Single(b => b["type"]!.GetValue<string>() == "job");
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", job["traceId"]!.GetValue<string>());
        Assert.Equal("00f067aa0ba902b7", job["parentSpanId"]!.GetValue<string>());
        Assert.Equal("processed", job["status"]!.GetValue<string>());
        var entries = job["entries"]!.AsArray().Select(e => e!.AsObject()).ToList();
        var span = entries.Single(e => e["type"]!.GetValue<string>() == "span");
        var query = entries.Single(e => e["type"]!.GetValue<string>() == "query");
        var log = entries.Single(e => e["type"]!.GetValue<string>() == "log");
        Assert.Equal(span["id"]!.GetValue<string>(), query["parentId"]!.GetValue<string>());
        Assert.True(span["durationMs"]!.GetValue<double>() >= 4);
        Assert.Equal("********", log["content"]!["context"]!["password"]!.GetValue<string>());
        Assert.Equal("worker", transport.All().Last(r => r.Path == "/partner/telescope").Object["service"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_job_is_recorded_as_failed_with_its_exception()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;
        using var telescope = new NexusTelescope(client, new TelescopeOptions { SampleRate = 0 });
        telescope.Start();

        await Assert.ThrowsAsync<InvalidOperationException>(() => telescope.RunAsync("job", "Charge", () => throw new InvalidOperationException("card declined")));
        await telescope.RunAsync("job", "Fine", () => Task.CompletedTask);
        await telescope.FlushAsync();

        var jobs = Batches(transport);
        var failed = Assert.Single(jobs); // the successful one was sampled out
        Assert.Equal("failed", failed["status"]!.GetValue<string>());
        var exception = failed["entries"]!.AsArray().Single(e => e!["type"]!.GetValue<string>() == "exception")!;
        Assert.Equal("card declined", exception["content"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Aspnetcore_requests_record_route_status_bodies_and_outgoing_calls_that_join_the_trace()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<INexusClient>(client);
        builder.Services.AddNexusTelescope(o => o.Service = "shop-dotnet");
        builder.Services.AddHttpClient();
        await using var app = builder.Build();
        app.UseNexusTelescope();
        app.MapGet("/ping", (HttpContext ctx) => Results.Json(new { tp = ctx.Request.Headers["traceparent"].ToString() }));
        app.MapPost("/orders/{id:int}", async (int id, IHttpClientFactory http, ILogger<TelescopeTests> log, HttpContext ctx) =>
        {
            log.LogWarning("placing order {OrderId}", id);
            var self = ctx.Request.Scheme + "://" + ctx.Request.Host;
            var ping = await http.CreateClient().GetStringAsync(self + "/ping");
            return Results.Json(new { id, ping });
        });
        app.MapGet("/boom", () => { throw new InvalidOperationException("kaput"); });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = new HttpClient();
        using var body = new StringContent("""{"qty":2,"card_number":"4111"}""", System.Text.Encoding.UTF8, "application/json");
        var placed = await http.PostAsync(address + "/orders/7", body);
        Assert.True(placed.IsSuccessStatusCode);
        var failed = await http.GetAsync(address + "/boom");
        Assert.Equal(500, (int)failed.StatusCode);
        await Task.Delay(100);
        await app.Services.GetRequiredService<NexusTelescope>().FlushAsync();
        await app.StopAsync();

        var batches = Batches(transport);
        var order = batches.Single(b => b["content"]?["path"]?.GetValue<string>() == "/orders/7");
        Assert.Equal("/orders/{id:int}", order["content"]!["route"]!.GetValue<string>());
        Assert.Equal(200, order["content"]!["responseStatus"]!.GetValue<int>());
        Assert.Equal("********", order["content"]!["payload"]!["card_number"]!.GetValue<string>());
        Assert.Equal(7, order["content"]!["response"]!["id"]!.GetValue<int>());
        var entries = order["entries"]!.AsArray().Select(e => e!.AsObject()).ToList();
        Assert.Contains(entries, e => e["type"]!.GetValue<string>() == "log" && e["content"]!["message"]!.GetValue<string>() == "placing order 7");
        var call = entries.Single(e => e["type"]!.GetValue<string>() == "http");
        Assert.Equal("200", call["status"]!.GetValue<string>());

        // The downstream request (same app) joined the trace, parented on the call.
        var ping = batches.Single(b => b["content"]?["path"]?.GetValue<string>() == "/ping");
        Assert.Equal(order["traceId"]!.GetValue<string>(), ping["traceId"]!.GetValue<string>());
        Assert.Equal(call["id"]!.GetValue<string>(), ping["parentSpanId"]!.GetValue<string>());

        var boom = batches.Single(b => b["content"]?["path"]?.GetValue<string>() == "/boom");
        Assert.Equal("500", boom["status"]!.GetValue<string>());
        var exceptions = boom["entries"]!.AsArray().Where(e => e!["type"]!.GetValue<string>() == "exception").ToList();
        Assert.Single(exceptions); // middleware + logger both saw it; recorded once
    }
}
