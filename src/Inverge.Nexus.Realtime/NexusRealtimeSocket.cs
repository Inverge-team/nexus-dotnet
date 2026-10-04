using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SocketIO.Core;
using SocketIOClient;
using SocketIOClient.Transport;

// The library's root namespace is `SocketIO`, which shadows its own
// `SocketIOClient.SocketIO` connection type once SocketIO.Core is in scope.
// Aliasing it keeps both usable without fully qualifying every reference.
using SocketIOConnection = SocketIOClient.SocketIO;

namespace Inverge.Nexus.Realtime;

/// <summary>
/// A Socket.IO connection to the Nexus realtime plane, for receiving room traffic.
/// </summary>
/// <remarks>
/// <para>
/// The core SDK's <c>Realtime</c> resource only emits. Use this when a .NET process
/// needs to <em>listen</em> — a worker reacting to room traffic, a bridge into
/// another system, a test harness.
/// </para>
/// <para>
/// Rooms are <strong>environment-scoped</strong>. The key you connect with must
/// belong to the same environment that emits, or you will sit in silence that looks
/// exactly like a bug in your handler.
/// </para>
/// <para>
/// Handlers registered before connecting are attached on connect, and joined rooms
/// are re-joined after every reconnect — server-side membership does not survive
/// one, so without that a reconnected socket goes quiet permanently.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var socket = new NexusRealtimeSocket(nexus);
///
/// socket.On("status", message => Console.WriteLine(message.Payload));
///
/// await socket.ConnectAsync();
/// await socket.JoinAsync("orders:42");
/// </code>
/// </example>
public sealed class NexusRealtimeSocket : IAsyncDisposable
{
    private readonly NexusOptions _nexusOptions;
    private readonly NexusRealtimeOptions _options;
    private readonly Dictionary<string, List<Func<NexusRealtimeMessage, Task>>> _handlers
        = new Dictionary<string, List<Func<NexusRealtimeMessage, Task>>>(StringComparer.Ordinal);

    private readonly HashSet<string> _rooms = new HashSet<string>(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
    private readonly object _gate = new object();

    private SocketIOConnection? _socket;
    private bool _stopped;
    private int _disposed;

    /// <summary>Creates a socket from a configured client.</summary>
    public NexusRealtimeSocket(INexusClient client, Action<NexusRealtimeOptions>? configure = null)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).Options,
            configure)
    {
    }

    /// <summary>Creates a socket from options.</summary>
    public NexusRealtimeSocket(NexusOptions options, Action<NexusRealtimeOptions>? configure = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.Validate();
        _nexusOptions = options;
        _options = new NexusRealtimeOptions();
        configure?.Invoke(_options);
    }

    /// <summary>Raised once the handshake succeeds, including after a reconnect.</summary>
    public event EventHandler? Connected;

    /// <summary>Raised when the connection drops, carrying the reason.</summary>
    public event EventHandler<string>? Disconnected;

    /// <summary>Raised when the server refuses the connection or reports an error.</summary>
    public event EventHandler<NexusRealtimeErrorEventArgs>? Error;

    /// <summary>Whether the socket is currently connected.</summary>
    public bool IsConnected => _socket?.Connected == true;

    /// <summary>The server's id for this socket, once connected.</summary>
    public string? SocketId => _socket?.Id;

    /// <summary>The rooms this socket believes it has joined.</summary>
    public IReadOnlyCollection<string> Rooms
    {
        get
        {
            lock (_gate)
            {
                return new List<string>(_rooms);
            }
        }
    }

    /// <summary>
    /// Registers an async handler for an event name. Several handlers per event are
    /// allowed.
    /// </summary>
    public void On(string eventName, Func<NexusRealtimeMessage, Task> handler)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("An event name is required.", nameof(eventName));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        bool isFirst;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventName, out var handlers))
            {
                handlers = new List<Func<NexusRealtimeMessage, Task>>();
                _handlers[eventName] = handlers;
            }

            isFirst = handlers.Count == 0;
            handlers.Add(handler);
        }

        // One Socket.IO subscription per event name fans out to our own list, so
        // adding a second handler does not register a duplicate listener.
        if (isFirst)
        {
            _socket?.On(eventName, response => DispatchAsync(eventName, response));
        }
    }

    /// <summary>Registers a synchronous handler for an event name.</summary>
    public void On(string eventName, Action<NexusRealtimeMessage> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        On(eventName, message =>
        {
            handler(message);
            return Task.CompletedTask;
        });
    }

    /// <summary>Removes every handler for an event name.</summary>
    public void Off(string eventName)
    {
        lock (_gate)
        {
            _handlers.Remove(eventName);
        }

        _socket?.Off(eventName);
    }

    /// <summary>Opens the connection. Idempotent.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                throw new InvalidOperationException(
                    "This realtime socket has been stopped and cannot be reconnected. Create a new one.");
            }

            if (_socket?.Connected == true)
            {
                return;
            }

            var socket = _socket ?? Build();
            _socket = socket;

            await socket.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Closes the connection.</summary>
    public async Task DisconnectAsync()
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        try
        {
            await socket.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            OnError("Disconnect failed: " + exception.Message, exception);
        }
    }

    /// <summary>
    /// Joins a room. Idempotent, and re-applied automatically after a reconnect.
    /// </summary>
    public async Task<JsonNode?> JoinAsync(string room, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            throw new ArgumentException("A room name is required.", nameof(room));
        }

        lock (_gate)
        {
            _rooms.Add(room);
        }

        return await CallAsync(
            "room.join",
            new JsonObject { ["name"] = JsonValue.Create(room) },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Leaves a room, and stops re-joining it on reconnect.</summary>
    public async Task<JsonNode?> LeaveAsync(string room, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            throw new ArgumentException("A room name is required.", nameof(room));
        }

        lock (_gate)
        {
            _rooms.Remove(room);
        }

        return await CallAsync(
            "room.leave",
            new JsonObject { ["name"] = JsonValue.Create(room) },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits over the socket.
    /// </summary>
    /// <remarks>
    /// From a server, the HTTP <c>client.Realtime.EmitAsync</c> is usually the better
    /// choice: it needs no live connection and it is retried and reported like any
    /// other API call.
    /// </remarks>
    public Task<JsonNode?> EmitAsync(
        string room, string eventName, object? payload = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            throw new ArgumentException("A room name is required.", nameof(room));
        }

        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("An event name is required.", nameof(eventName));
        }

        var body = new JsonObject
        {
            ["name"] = JsonValue.Create(room),
            ["event"] = JsonValue.Create(eventName),
            ["payload"] = payload is null ? null : JsonSerializer.SerializeToNode(payload),
        };

        return CallAsync("room.emit", body, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopped = true;
        await DisconnectAsync().ConfigureAwait(false);

        _socket?.Dispose();
        _connectGate.Dispose();
    }

    private SocketIOConnection Build()
    {
        var auth = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // `key` is the field every Nexus SDK sends. The server also accepts
            // `apiKey` and an x-api-key header; browsers cannot set handshake
            // headers, which is why the auth field is the primary channel.
            ["key"] = _nexusOptions.ApiKey,
        };

        var ambient = NexusContext.Current;
        Add(auth, "distinctId", _options.DistinctId ?? ambient.DistinctId);
        Add(auth, "sessionKey", _options.SessionKey ?? ambient.SessionKey);
        Add(auth, "deviceKey", _options.DeviceKey ?? ambient.DeviceKey);
        Add(auth, "os", ambient.OsType ?? _nexusOptions.OsType);
        Add(auth, "osVersion", ambient.OsVersion ?? _nexusOptions.OsVersion);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = _nexusOptions.ApiKey,
            ["User-Agent"] = _nexusOptions.ResolvedUserAgent,
        };

        foreach (var header in _options.ExtraHeaders)
        {
            headers[header.Key] = header.Value;
        }

        var transport = new SocketIOOptions
        {
            // The backend runs Socket.IO 4 / Engine.IO 4. Pinning it means a changed
            // library default cannot silently break the handshake.
            EIO = EngineIO.V4,
            Reconnection = _options.Reconnection,
            ReconnectionAttempts = _options.ReconnectionAttempts,
            ReconnectionDelayMax = (int)_options.ReconnectionDelayMax.TotalMilliseconds,
            ConnectionTimeout = _options.ConnectionTimeout,
            Auth = auth,
        };

        if (_options.PreferWebSocket)
        {
            transport.Transport = TransportProtocol.WebSocket;
        }

        foreach (var header in headers)
        {
            transport.ExtraHeaders[header.Key] = header.Value;
        }

        var url = _options.Url ?? _nexusOptions.SocketBase;
        var socket = new SocketIOConnection(new Uri(url), transport);

        socket.OnConnected += (sender, args) => HandleConnected();
        socket.OnReconnected += (sender, attempt) => HandleConnected();
        socket.OnDisconnected += (sender, reason) => Disconnected?.Invoke(this, reason ?? string.Empty);
        socket.OnError += (sender, message) => HandleError(message);
        socket.OnReconnectError += (sender, exception) =>
            OnError("Reconnect failed: " + exception?.Message, exception);

        lock (_gate)
        {
            foreach (var pair in _handlers)
            {
                var eventName = pair.Key;
                socket.On(eventName, response => DispatchAsync(eventName, response));
            }
        }

        return socket;
    }

    private static void Add(Dictionary<string, object?> auth, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            auth[key] = value;
        }
    }

    private void HandleConnected()
    {
        // Room membership lives on the server socket and does not survive a
        // reconnect. Re-joining is what keeps a long-lived listener working across
        // a deploy or a network blip instead of going permanently silent.
        List<string> rooms;
        lock (_gate)
        {
            rooms = new List<string>(_rooms);
        }

        if (rooms.Count > 0)
        {
            _ = RejoinAsync(rooms);
        }

        Connected?.Invoke(this, EventArgs.Empty);
    }

    private async Task RejoinAsync(List<string> rooms)
    {
        foreach (var room in rooms)
        {
            try
            {
                await CallAsync(
                    "room.join",
                    new JsonObject { ["name"] = JsonValue.Create(room) },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                OnError(
                    string.Format(
                        CultureInfo.InvariantCulture, "Re-joining room '{0}' failed.", room),
                    exception);
            }
        }
    }

    private void HandleError(string? message)
    {
        var args = new NexusRealtimeErrorEventArgs(message ?? string.Empty);
        Error?.Invoke(this, args);

        if (args.IsBillingSuspended && _options.StopOnBillingSuspended)
        {
            // Retrying cannot clear a suspension. Stop, so the one useful error is
            // not drowned out by a reconnect every few seconds for however long the
            // invoice stays unpaid.
            _stopped = true;
            _ = DisconnectAsync();
        }
    }

    private void OnError(string message, Exception? exception)
        => Error?.Invoke(this, new NexusRealtimeErrorEventArgs(message, exception));

    private async Task<JsonNode?> CallAsync(
        string message, JsonObject body, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || !socket.Connected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            socket = _socket;
        }

        if (socket is null)
        {
            return null;
        }

        var completion = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await socket.EmitAsync(
            message,
            response =>
            {
                completion.TrySetResult(ReadPayload(response));
                return Task.CompletedTask;
            },
            body).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(_options.AckTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var registration = linked.Token.Register(() => completion.TrySetCanceled());

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The server never acknowledged. The emit may still have landed, so this
            // is reported rather than thrown — a missing ack is not proof of failure.
            OnError(message + " was not acknowledged within the ack timeout.", null);
            return null;
        }
    }

    private async Task DispatchAsync(string eventName, SocketIOResponse response)
    {
        List<Func<NexusRealtimeMessage, Task>> handlers;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventName, out var registered) || registered.Count == 0)
            {
                return;
            }

            handlers = new List<Func<NexusRealtimeMessage, Task>>(registered);
        }

        var message = new NexusRealtimeMessage(eventName, ReadPayload(response), SafeRawText(response));

        foreach (var handler in handlers)
        {
            try
            {
                await handler(message).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // One bad handler must not stop the others, and must not kill the
                // socket's receive loop.
                OnError("A realtime handler for '" + eventName + "' threw.", exception);
            }
        }
    }

    private static JsonNode? ReadPayload(SocketIOResponse response)
    {
        try
        {
            var element = response.GetValue<JsonElement>(0);
            return element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null
                ? null
                : JsonNode.Parse(element.GetRawText());
        }
        catch (Exception)
        {
            // An emit with no payload, or one that will not parse. The raw frame
            // text is still available for whoever needs it.
            return null;
        }
    }

    private static string? SafeRawText(SocketIOResponse response)
    {
        try
        {
            return response.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
