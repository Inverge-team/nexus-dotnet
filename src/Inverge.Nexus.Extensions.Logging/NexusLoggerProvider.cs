using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>
/// An <c>ILogger</c> provider that writes to the Nexus logs product.
/// </summary>
/// <remarks>
/// Register it with <c>builder.Logging.AddNexus()</c>. Every existing
/// <c>logger.LogInformation(...)</c> in the application then lands in Nexus with no
/// call-site changes, and records carrying an exception also become grouped issues
/// in error monitoring.
/// </remarks>
[ProviderAlias("Nexus")]
public sealed class NexusLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, NexusLogger> _loggers
        = new ConcurrentDictionary<string, NexusLogger>(StringComparer.Ordinal);

    private readonly NexusClientAccessor _accessor;
    private readonly NexusLoggingOptions _options;
    private IExternalScopeProvider? _scopes;

    /// <summary>Creates the provider.</summary>
    /// <param name="services">Used to resolve the client lazily, on the first record.</param>
    /// <param name="options">What to forward.</param>
    public NexusLoggerProvider(IServiceProvider services, IOptions<NexusLoggingOptions> options)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        _accessor = new NexusClientAccessor(services);
        _options = options?.Value ?? new NexusLoggingOptions();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(
            categoryName ?? string.Empty,
            category => new NexusLogger(category, _accessor, _options, () => _scopes));

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <summary>
    /// Releases the provider's loggers.
    /// </summary>
    /// <remarks>
    /// The client is not disposed here. It is a container singleton with its own
    /// lifetime, and the logging provider is usually torn down first — disposing it
    /// from here would stop telemetry while the rest of the application is still
    /// shutting down and still producing it.
    /// </remarks>
    public void Dispose() => _loggers.Clear();
}
