using System;

namespace Inverge.Nexus.Diagnostics;

/// <summary>Severity of a message the SDK emits about itself.</summary>
public enum NexusDiagnosticLevel
{
    /// <summary>Verbose detail, emitted only when <c>Debug</c> is on.</summary>
    Debug = 0,

    /// <summary>Normal lifecycle detail.</summary>
    Information = 1,

    /// <summary>Something failed but the SDK carried on — a dropped batch, a retried call.</summary>
    Warning = 2,

    /// <summary>Something failed and could not be worked around.</summary>
    Error = 3,
}

/// <summary>
/// Receives the SDK's own diagnostics.
/// </summary>
/// <remarks>
/// The core package has no logging dependency, so it reports through this
/// delegate instead. <c>Inverge.Nexus.Extensions.Logging</c> wires it to
/// <c>ILogger</c> for you; set <see cref="NexusOptions.DiagnosticSink"/> directly
/// if you are not using Microsoft.Extensions.
/// </remarks>
/// <param name="level">How bad it is.</param>
/// <param name="message">What happened.</param>
/// <param name="exception">The cause, when there was one.</param>
public delegate void NexusDiagnosticSink(NexusDiagnosticLevel level, string message, Exception? exception);
