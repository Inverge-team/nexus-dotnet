using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Telescope;

/// <summary>Activity → Telescope entry, by OpenTelemetry semantic conventions (old and new names).</summary>
internal static class ActivityMapper
{
    /// <summary>The unit-of-work type a local-root activity opens, or null if it is not one.</summary>
    public static string? UnitOfWork(Activity a)
    {
        if (a.Source == NexusTelescope.Source && a.GetTagItem(NexusTelescope.TypeTag) is string t)
        {
            return t;
        }

        if (a.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn")
        {
            return "request";
        }

        return a.Kind switch
        {
            ActivityKind.Server => "request",
            ActivityKind.Consumer => "job",
            _ => null,
        };
    }

    /// <summary>Activities recorded from richer diagnostic events instead (HttpClient, and DB once EF Core is seen).</summary>
    public static bool HandledElsewhere(Activity a, DiagnosticSubscriber? diagnostics)
    {
        if (a.OperationName == "System.Net.Http.HttpRequestOut" || a.Source.Name == "System.Net.Http")
        {
            return diagnostics?.HttpClientSeen == true;
        }

        return diagnostics?.EfCoreSeen == true && Tag(a, "db.system", "db.system.name") is not null;
    }

    public static (string Type, JsonObject Content, string? Status) Entry(Activity a)
    {
        var failed = a.Status == ActivityStatusCode.Error;
        var content = new JsonObject { ["name"] = a.DisplayName, ["source"] = a.Source.Name, ["kind"] = a.Kind.ToString() };
        if (failed)
        {
            content["error"] = a.StatusDescription ?? "error";
        }

        var method = Tag(a, "http.request.method", "http.method");
        var db = Tag(a, "db.system.name", "db.system");
        if (method is not null && a.Kind == ActivityKind.Client)
        {
            var status = Tag(a, "http.response.status_code", "http.status_code");
            content["method"] = method;
            content["url"] = Tag(a, "url.full", "http.url") ?? a.DisplayName;
            content["responseStatus"] = status;
            return ("http", content, status ?? (failed ? "failed" : null));
        }

        if (db is not null)
        {
            var text = Tag(a, "db.query.text", "db.statement") ?? a.DisplayName;
            if (string.Equals(db, "redis", StringComparison.OrdinalIgnoreCase))
            {
                content["command"] = text;
                content["connection"] = "redis";
                return ("redis", content, null);
            }

            var ns = Tag(a, "db.namespace", "db.name");
            content["sql"] = text;
            content["connection"] = ns is null ? db : $"{db}/{ns}";
            return ("query", content, null);
        }

        if (Tag(a, "messaging.system") is { } system && (a.Kind == ActivityKind.Producer))
        {
            content["name"] = a.DisplayName;
            content["queue"] = Tag(a, "messaging.destination.name", "messaging.destination");
            content["connection"] = system;
            content["status"] = "dispatched";
            return ("job", content, "dispatched");
        }

        foreach (var tag in a.TagObjects)
        {
            content["attributes"] ??= new JsonObject();
            ((JsonObject)content["attributes"]!)[tag.Key] = NexusTelescope.ToNode(tag.Value);
        }

        return ("span", content, failed ? "error" : null);
    }

    private static string? Tag(Activity a, params string[] names)
    {
        foreach (var n in names)
        {
            if (a.GetTagItem(n) is { } v)
            {
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}
