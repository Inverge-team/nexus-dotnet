using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Error monitoring — report exceptions and error conditions.</summary>
public sealed class ErrorsResource : NexusResource
{
    private const int MaxFrames = 100;
    private const int MaxInnerExceptions = 3;

    internal ErrorsResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Reports an error by message.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="type">
    /// The error type, e.g. <c>GatewayTimeout</c>. Grouping keys off this and the
    /// fingerprint, so a stable type keeps related failures in one issue.
    /// </param>
    /// <param name="level">
    /// <c>error</c>, <c>warn</c> or <c>fatal</c>.
    /// </param>
    /// <param name="handled">
    /// Whether your code caught and dealt with it. Unhandled errors are what the
    /// console surfaces first.
    /// </param>
    /// <param name="fingerprint">
    /// An explicit grouping key. Set it when the default grouping splits one real
    /// problem across many issues — a message containing an order id, say.
    /// </param>
    /// <param name="stack">Stack frames, innermost first.</param>
    /// <param name="errorContext">Any structured context worth having while debugging.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ErrorCaptureResult> CaptureAsync(
        string message,
        string? type = null,
        string? level = null,
        bool? handled = null,
        string? fingerprint = null,
        IEnumerable<NexusStackFrame>? stack = null,
        object? errorContext = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(message, nameof(message));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.ErrorsCapture(
                resolved, message, type, level, handled, fingerprint, FramesToJson(stack), errorContext),
            cancellationToken).ConfigureAwait(false);

        return ErrorCaptureResult.From(response);
    }

    /// <summary>Reports a caught exception, with its type, message and stack.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="handled">
    /// Whether you caught it deliberately. Defaults to <c>true</c>, since calling
    /// this at all usually means you did.
    /// </param>
    /// <param name="level">Severity; defaults to <c>error</c>.</param>
    /// <param name="fingerprint">An explicit grouping key, if the default grouping is wrong.</param>
    /// <param name="includeInner">
    /// Walk <see cref="Exception.InnerException"/> and append its frames. This is
    /// usually where the real cause is, so it defaults to on.
    /// </param>
    /// <param name="errorContext">Structured context worth having while debugging.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <example>
    /// <code>
    /// try { await ChargeAsync(order); }
    /// catch (PaymentException ex)
    /// {
    ///     await nexus.Errors.CaptureExceptionAsync(ex, errorContext: new { order = order.Id });
    /// }
    /// </code>
    /// </example>
    public Task<ErrorCaptureResult> CaptureExceptionAsync(
        Exception exception,
        bool handled = true,
        string? level = "error",
        string? fingerprint = null,
        bool includeInner = true,
        object? errorContext = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var frames = new List<NexusStackFrame>(StackFrames(exception));

        if (includeInner)
        {
            AppendInner(frames, exception);
        }

        return CaptureAsync(
            string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message,
            exception.GetType().Name,
            level,
            handled,
            fingerprint,
            frames,
            errorContext,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Normalised stack frames for an exception, innermost first — the order the
    /// console reads, so the first line is where it broke.
    /// </summary>
    public static IReadOnlyList<NexusStackFrame> StackFrames(Exception exception, int limit = MaxFrames)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var frames = new List<NexusStackFrame>();

        // File and line numbers need the PDB alongside the assembly. Without one
        // the frames still carry method names, which is enough to group and to
        // symbolicate later from an uploaded symbol file.
        var trace = new StackTrace(exception, fNeedFileInfo: true);

        foreach (var frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            if (frame is null)
            {
                continue;
            }

            if (frames.Count >= limit)
            {
                break;
            }

            var line = frame.GetFileLineNumber();
            frames.Add(new NexusStackFrame(
                DescribeFrame(frame),
                frame.GetFileName(),
                line > 0 ? line : null));
        }

        return frames;
    }

    /// <summary>
    /// A stable grouping key: the exception type plus the deepest frame that threw.
    /// </summary>
    /// <remarks>
    /// Use it when two genuinely different failures share a message, or when a
    /// message varies per request and would otherwise create an issue per call.
    /// </remarks>
    public static string Fingerprint(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var frames = StackFrames(exception, limit: 1);
        if (frames.Count == 0)
        {
            return exception.GetType().Name;
        }

        var frame = frames[0];
        var where = frame.Filename is null
            ? frame.Function ?? "?"
            : string.Format(
                CultureInfo.InvariantCulture, "{0}:{1}", frame.Filename, frame.LineNumber ?? 0);

        return exception.GetType().Name + "@" + where;
    }

    private static void AppendInner(List<NexusStackFrame> frames, Exception exception)
    {
        var inner = exception.InnerException;
        var depth = 0;

        while (inner is not null && depth < MaxInnerExceptions)
        {
            // A marker frame, so the console shows where one exception ends and
            // its cause begins instead of presenting one merged stack.
            frames.Add(new NexusStackFrame(
                "caused by " + inner.GetType().Name + ": " + inner.Message));
            frames.AddRange(StackFrames(inner));

            // An AggregateException's first inner exception is the one that
            // matters in practice; the rest are reachable through Raw.
            inner = inner is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                ? aggregate.InnerExceptions[0]
                : inner.InnerException;

            depth++;
        }
    }

    /// <remarks>
    /// <see cref="StackFrame.GetMethod"/> reads metadata a trimmer may have
    /// removed. The consequence is bounded and acceptable: a trimmed build reports
    /// the exception with fewer method names, which still groups and still points at
    /// the right file and line. Losing error reporting entirely to avoid that would
    /// be the worse trade.
    /// </remarks>
#if !NETSTANDARD2_0
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Stack frame metadata may be trimmed; the frame is still reported without its method name.")]
#endif
    private static string? DescribeFrame(StackFrame frame) => Describe(frame.GetMethod());

    private static string? Describe(MethodBase? method)
    {
        if (method is null)
        {
            return null;
        }

        var declaring = method.DeclaringType?.FullName;
        return declaring is null
            ? method.Name
            : new StringBuilder(declaring).Append('.').Append(method.Name).ToString();
    }

    private static JsonArray? FramesToJson(IEnumerable<NexusStackFrame>? frames)
    {
        if (frames is null)
        {
            return null;
        }

        var array = new JsonArray();
        foreach (var frame in frames)
        {
            array.Add((JsonNode?)JsonBody.Create()
                .Set("function", frame.Function)
                .Set("filename", frame.Filename)
                .Set("lineno", frame.LineNumber)
                .Set("context_line", frame.ContextLine)
                .Build());
        }

        return array.Count == 0 ? null : array;
    }
}
