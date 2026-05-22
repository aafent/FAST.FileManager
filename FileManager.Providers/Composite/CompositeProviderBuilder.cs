using FAST.FileManager.Abstractions;
using FAST.FileManager.Providers.LocalFileSystem;
using FAST.FileManager.Providers.S3;
using Microsoft.Extensions.Configuration;

namespace FAST.FileManager.Providers.Composite;

/// <summary>
/// Fluent builder used inside <c>AddCompositeFileProvider</c> to register
/// one or more providers that will be aggregated by
/// <see cref="CompositeFileProvider"/>.
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
public sealed class CompositeProviderBuilder
{
    private readonly List<ProviderRegistration> _registrations = new();

    internal IReadOnlyList<ProviderRegistration> Registrations => _registrations;

    /// <summary>
    /// Adds the S3-compatible provider.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="alias">
    /// Alias used as prefix on volume ID conflicts. Defaults to <c>"s3"</c>.
    /// </param>
    public CompositeProviderBuilder AddS3Provider(
        IConfiguration configuration,
        string alias = "s3")
    {
        var options = new S3ProviderOptions();
        configuration.GetSection(S3ProviderOptions.SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException(
                $"S3 configuration is missing. Ensure the " +
                $"'{S3ProviderOptions.SectionName}' section has a non-empty Endpoint.");

        var signer  = new SigV4Signer(options.AccessKey, options.SecretKey, options.Region);
        var http    = new System.Net.Http.HttpClient();
        var client  = new S3Client(http, signer, options.Endpoint);
        var provider = new S3FileProvider(client, options);

        _registrations.Add(new ProviderRegistration(provider, alias));
        return this;
    }

    /// <summary>
    /// Adds the local filesystem provider.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="alias">
    /// Alias used as prefix on volume ID conflicts.
    /// Defaults to <c>"localfilesystem"</c>.
    /// </param>
    public CompositeProviderBuilder AddLocalFileSystemProvider(
        IConfiguration configuration,
        string alias = "localfilesystem")
    {
        var options = new LocalFileSystemOptions();
        configuration
            .GetSection(LocalFileSystemOptions.SectionName)
            .Bind(options);

        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new InvalidOperationException(
                $"LocalFileSystem configuration is missing. Ensure the " +
                $"'{LocalFileSystemOptions.SectionName}' section has a non-empty RootPath.");

        var provider = new LocalFileSystemProvider(options);
        _registrations.Add(new ProviderRegistration(provider, alias));
        return this;
    }

    /// <summary>
    /// Adds any custom <see cref="IFileProvider"/> implementation.
    /// </summary>
    public CompositeProviderBuilder AddProvider(
        IFileProvider provider,
        string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Alias must not be empty.", nameof(alias));

        _registrations.Add(new ProviderRegistration(provider, alias));
        return this;
    }
}
