using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace Inverge.Nexus.Telescope;

/// <summary>One unit of work being recorded, keyed by its local-root <see cref="Activity"/>.</summary>
internal sealed class TelescopeBatch
{
    private readonly List<JsonObject> _entries = new List<JsonObject>();
    private int _done;

    public TelescopeBatch(Activity root, string type)
    {
        Root = root;
        Type = type;
        Id = root.SpanId.ToHexString();
        TraceId = root.TraceId.ToHexString();
        // A local root with a parent context continues someone else's trace (an
        // incoming traceparent, or a job's producer — parsed contexts are not marked remote).
        ParentSpanId = root.Parent is null && root.ParentSpanId != default ? root.ParentSpanId.ToHexString() : null;
        StartUtc = root.StartTimeUtc;
        Name = type == "request" ? null : root.DisplayName;
    }

    public object Gate { get; } = new object();

    public Activity Root { get; }

    public string Type { get; }

    public string Id { get; }

    public string TraceId { get; }

    public string? ParentSpanId { get; }

    public DateTime StartUtc { get; }

    public string? Name { get; set; }

    public string? Status { get; set; }

    public double DurationMs { get; set; }

    public bool Failed { get; set; }

    public int Dropped { get; private set; }

    public JsonObject Content { get; } = new JsonObject();

    public List<string> Tags { get; } = new List<string>();

    public JsonObject? User { get; set; }

    public bool Done => Volatile.Read(ref _done) == 1;

    public bool MarkDone() => Interlocked.Exchange(ref _done, 1) == 0;

    public void SetStatus(string status)
    {
        lock (Gate)
        {
            Status ??= status;
        }
    }

    public void Add(JsonObject entry, int max)
    {
        lock (Gate)
        {
            if (_entries.Count >= max)
            {
                Dropped++;
                return;
            }

            _entries.Add(entry);
        }
    }

    public bool IsFailure()
    {
        var s = Status ?? Content["responseStatus"]?.ToString();
        if (s is null)
        {
            return false;
        }

        return Type switch
        {
            "request" => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code >= 500,
            "job" => s == "failed",
            "command" or "schedule" => s != "0",
            _ => false,
        };
    }

    public JsonObject ToWire()
    {
        lock (Gate)
        {
            var content = (JsonObject)Content.DeepClone();
            if (Dropped > 0)
            {
                content["droppedEntries"] = Dropped;
            }

            var entries = new JsonArray();
            foreach (var e in _entries)
            {
                entries.Add(e);
            }

            _entries.Clear();
            var tags = new JsonArray();
            var userTag = User?["id"]?.GetValue<string>();
            foreach (var t in Tags.Distinct(StringComparer.Ordinal))
            {
                tags.Add(t);
            }

            var wire = new JsonObject
            {
                ["id"] = Id,
                ["type"] = Type,
                ["traceId"] = TraceId,
                ["startedAt"] = Math.Round((StartUtc - DateTime.UnixEpoch).TotalMilliseconds, 3),
                ["durationMs"] = Math.Round(DurationMs, 3),
                ["content"] = content,
                ["tags"] = tags,
                ["entries"] = entries,
            };
            if (ParentSpanId is not null)
            {
                wire["parentSpanId"] = ParentSpanId;
            }

            if (Name is not null)
            {
                wire["name"] = Name;
            }

            if (Status is not null)
            {
                wire["status"] = Status;
            }

            if (User is not null && userTag is not null)
            {
                wire["user"] = User.DeepClone();
            }

            return wire;
        }
    }
}

/// <summary>The environment's Telescope settings, as last returned by Nexus.</summary>
internal sealed class RemoteSettings
{
    public static readonly RemoteSettings Default = new RemoteSettings();

    public bool Enabled { get; private set; } = true;

    public double SampleRate { get; private set; } = 1;

    public double SlowRequestMs { get; private set; } = 1000;

    public IReadOnlyList<string> IgnorePaths { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<string> IgnoreTypes { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<string> RedactKeys { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<string> MonitoredTags { get; private set; } = Array.Empty<string>();

    public static RemoteSettings From(JsonObject c)
    {
        static IReadOnlyList<string> List(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : Array.Empty<string>();
        return new RemoteSettings
        {
            Enabled = c["enabled"]?.GetValue<bool>() ?? true,
            SampleRate = c["sampleRate"]?.GetValue<double>() ?? 1,
            SlowRequestMs = c["slowRequestMs"]?.GetValue<double>() ?? 1000,
            IgnorePaths = List(c["ignorePaths"]),
            IgnoreTypes = List(c["ignoreTypes"]),
            RedactKeys = List(c["redactKeys"]),
            MonitoredTags = List(c["monitoredTags"]),
        };
    }
}
