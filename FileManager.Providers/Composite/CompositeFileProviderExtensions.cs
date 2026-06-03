using FAST.FileManager.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FAST.FileManager.Providers.Composite;

/// <summary>
/// Extension methods for registering the composite file provider.
/// </summary>
public static class CompositeFileProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="CompositeFileProvider"/> that aggregates
    /// multiple providers configured via the <paramref name="configure"/>
    /// builder. Registered as Singleton — providers are built once at first
    /// resolution; <see cref="System.Net.Http.HttpClient"/> instances are
    /// managed via <see cref="IHttpClientFactory"/> to avoid socket exhaustion.
    /// </summary>
    public static IServiceCollection AddCompositeFileProvider(
        this IServiceCollection services,
        Action<CompositeProviderBuilder> configure)
    {
        // Ensure IHttpClientFactory is available.
        services.AddHttpClient();

        services.AddSingleton<IFileProvider>(sp =>
        {
            var loggerFactory      = sp.GetRequiredService<ILoggerFactory>();
            var httpClientFactory  = sp.GetRequiredService<IHttpClientFactory>();
            var builder            = new CompositeProviderBuilder(loggerFactory, httpClientFactory);
            configure(builder);
            return new CompositeFileProvider(builder.Registrations);
        });

        return services;
    }
}
