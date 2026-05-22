using FAST.FileManager.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FAST.FileManager.Providers.LocalFileSystem;

/// <summary>
/// Extension methods for registering the local filesystem file provider.
/// </summary>
public static class LocalFileSystemServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local filesystem file provider, binding options from
    /// the <c>"LocalFileSystem"</c> section of <paramref name="configuration"/>.
    /// </summary>
    /// <example>
    /// In <c>Program.cs</c>:
    /// <code>
    /// builder.Services.AddLocalFileSystemProvider(builder.Configuration);
    /// </code>
    /// In <c>appsettings.json</c>:
    /// <code>
    /// {
    ///   "LocalFileSystem": {
    ///     "RootPath":       "C:\\FileManagerRoot",
    ///     "VolumeName":     "Local Storage",
    ///     "ReadOnly":       false,
    ///     "MaxUploadBytes": 104857600
    ///   }
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddLocalFileSystemProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new LocalFileSystemOptions();
        configuration
            .GetSection(LocalFileSystemOptions.SectionName)
            .Bind(options);

        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new InvalidOperationException(
                $"LocalFileSystem configuration is missing or incomplete. " +
                $"Ensure the '{LocalFileSystemOptions.SectionName}' section " +
                $"in appsettings.json has a non-empty RootPath value.");

        services.AddSingleton(options);
        services.AddScoped<IFileProvider>(_ =>
            new LocalFileSystemProvider(options));

        return services;
    }
}
