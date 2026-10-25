using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Telescope;

/// <summary>
/// Subscribes to the DiagnosticListener events that carry more than their
/// Activities do: HttpClient (request/response objects), EF Core (the command,
/// its parameters and duration) and SqlClient. Payloads are read by reflection —
/// this package must not reference those libraries.
/// </summary>
internal sealed class DiagnosticSubscriber : IObserver<DiagnosticListener>, IDisposable
{
    private readonly NexusTelescope _telescope;
    private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
    private readonly ConcurrentDictionary<Guid, long> _sqlStarts = new ConcurrentDictionary<Guid, long>();
    private IDisposable? _all;

    public DiagnosticSubscriber(NexusTelescope telescope) => _telescope = telescope;

    public bool HttpClientSeen { get; private set; }

    public bool EfCoreSeen { get; private set; }

    public void Subscribe() => _all = DiagnosticListener.AllListeners.Subscribe(this);

    public void OnNext(DiagnosticListener listener)
    {
        switch (listener.Name)
        {
            case "HttpHandlerDiagnosticListener":
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(new Observer(OnHttp), name => name.StartsWith("System.Net.Http.HttpRequestOut", StringComparison.Ordinal)));
                }

                break;
            case "Microsoft.EntityFrameworkCore":
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(new Observer(OnEf), name => name is "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted" or "Microsoft.EntityFrameworkCore.Database.Command.CommandError"));
                }

                break;
            case "SqlClientDiagnosticListener":
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(new Observer(OnSqlClient), name => name.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || name.EndsWith("WriteCommandAfter", StringComparison.Ordinal) || name.EndsWith("WriteCommandError", StringComparison.Ordinal)));
                }

                break;
        }
    }

    private void OnHttp(KeyValuePair<string, object?> e)
    {
        if (e.Key != "System.Net.Http.HttpRequestOut.Stop")
        {
            return;
        }

        HttpClientSeen = true;
        var activity = Activity.Current; // the HttpRequestOut activity, end time already set
        var request = Get<HttpRequestMessage>(e.Value, "Request");
        if (activity is null || request is null)
        {
            return;
        }

        var response = Get<HttpResponseMessage>(e.Value, "Response");
        var status = response is null ? "failed" : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var content = new JsonObject
        {
            ["method"] = request.Method.Method,
            ["url"] = request.RequestUri?.ToString(),
            ["requestHeaders"] = Headers(request.Headers),
            ["responseStatus"] = response is null ? null : (int)response.StatusCode,
            ["responseHeaders"] = response is null ? null : Headers(response.Headers),
        };
        if (response is null && Get<object>(e.Value, "RequestTaskStatus") is { } taskStatus)
        {
            content["error"] = taskStatus.ToString();
        }

        var duration = activity.Duration > TimeSpan.Zero ? activity.Duration : DateTime.UtcNow - activity.StartTimeUtc;
        _telescope.Record("http", content, duration.TotalMilliseconds, status, null, activity.StartTimeUtc, activity.SpanId.ToHexString(), activity.Parent);
    }

    private void OnEf(KeyValuePair<string, object?> e)
    {
        EfCoreSeen = true;
        var command = Get<DbCommand>(e.Value, "Command");
        if (command is null)
        {
            return;
        }

        var duration = Get<object>(e.Value, "Duration") is TimeSpan d ? d.TotalMilliseconds : 0;
        var content = Command(command, "efcore");
        if (e.Key.EndsWith("CommandError", StringComparison.Ordinal))
        {
            content["error"] = Get<Exception>(e.Value, "Exception")?.Message ?? "error";
        }

        _telescope.Record("query", content, duration, null, null, null, null, null);
    }

    private void OnSqlClient(KeyValuePair<string, object?> e)
    {
        if (EfCoreSeen)
        {
            return; // EF Core reports these itself, with better context
        }

        var id = Get<object>(e.Value, "OperationId") is Guid g ? g : Guid.Empty;
        var ts = Get<object>(e.Value, "Timestamp") is long t ? t : Stopwatch.GetTimestamp();
        if (e.Key.EndsWith("Before", StringComparison.Ordinal))
        {
            _sqlStarts[id] = ts;
            return;
        }

        var start = _sqlStarts.TryRemove(id, out var s) ? s : ts;
        var command = Get<DbCommand>(e.Value, "Command");
        if (command is null)
        {
            return;
        }

        var content = Command(command, "sqlserver");
        if (e.Key.EndsWith("Error", StringComparison.Ordinal))
        {
            content["error"] = Get<Exception>(e.Value, "Exception")?.Message ?? "error";
        }

        _telescope.Record("query", content, (ts - start) * 1000.0 / Stopwatch.Frequency, null, null, null, null, null);
    }

    private static JsonObject Command(DbCommand command, string fallbackConnection)
    {
        var bindings = new JsonArray();
        foreach (DbParameter p in command.Parameters)
        {
            bindings.Add(NexusTelescope.ToNode(p.Value is DBNull ? null : p.Value));
        }

        return new JsonObject
        {
            ["sql"] = command.CommandText,
            ["bindings"] = bindings,
            ["connection"] = command.Connection?.Database is { Length: > 0 } db ? db : fallbackConnection,
        };
    }

    private static JsonObject Headers(System.Net.Http.Headers.HttpHeaders headers)
    {
        var obj = new JsonObject();
        foreach (var h in headers)
        {
            obj[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
        }

        return obj;
    }

    private static T? Get<T>(object? payload, string property)
        where T : class
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return payload.GetType().GetProperty(property)?.GetValue(payload) as T;
        }
#pragma warning disable CA1031 // a payload shape we do not understand is skipped
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        _all?.Dispose();
        lock (_subscriptions)
        {
            foreach (var s in _subscriptions)
            {
                s.Dispose();
            }

            _subscriptions.Clear();
        }
    }

    private sealed class Observer : IObserver<KeyValuePair<string, object?>>
    {
        private readonly Action<KeyValuePair<string, object?>> _on;

        public Observer(Action<KeyValuePair<string, object?>> on) => _on = on;

        public void OnNext(KeyValuePair<string, object?> value)
        {
            try
            {
                _on(value);
            }
#pragma warning disable CA1031 // never throw into the instrumented library
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
