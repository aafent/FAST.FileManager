using FAST.FileManager.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FAST.FileManager.Providers.Composite;

/// <summary>
/// Extension methods for registering the composite file provider.
/// </summary>
public static class CompositeFileProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="CompositeFileProvider"/> that aggregates
    /// multiple providers configured via the <paramref name="configure"/>
    /// builder. Replaces any previously registered <see cref="IFileProvider"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCompositeFileProvider(providers =>
    /// {
    ///     providers.AddS3Provider(builder.Configuration);
    ///     providers.AddLocalFileSystemProvider(builder.Configuration, alias: "local");
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCompositeFileProvider(
        this IServiceCollection services,
        Action<CompositeProviderBuilder> configure)
    {
        var builder = new CompositeProviderBuilder();
        configure(builder);

        var composite = new CompositeFileProvider(builder.Registrations);

        services.AddScoped<IFileProvider>(_ => composite);

        return services;
    }
}
