using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Inverge.Nexus.AspNetCore;

/// <summary>Wires the Nexus middleware into an ASP.NET Core application.</summary>
public static class NexusAspNetCoreExtensions
{
    /// <summary>Configures how the middleware reads a request.</summary>
    public static IServiceCollection AddNexusAspNetCore(
        this IServiceCollection services, Action<NexusAspNetCoreOptions>? configure = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Adds the Nexus request middleware.
    /// </summary>
    /// <remarks>
    /// Place it after authentication, so the signed-in user is available to bind,
    /// and outside your own exception handling, so unhandled exceptions reach it.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddNexus(o => o.ApiKey = "nxs_live_xxx");
    ///
    /// var app = builder.Build();
    /// app.UseAuthentication();
    /// app.UseNexus();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseNexus(this IApplicationBuilder app)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        return app.UseMiddleware<NexusMiddleware>();
    }
}
