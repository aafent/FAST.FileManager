using FAST.FileManager.Abstractions;
using FAST.FileManager.Providers.LocalFileSystem;
using FAST.FileManager.Providers.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;

namespace FAST.FileManager.Providers.Composite;

/// <summary>
/// Fluent builder used inside <c>AddCompositeFileProvider</c> to register
/// one or more providers that will be aggregated by
/// <see cref="CompositeFileProvider"/>.
/// </summary>
/// <remarks>
/// Every registered provider must have a unique alias. The alias is used
/// to prefix volume IDs when conflicts arise, and to distinguish multiple
/// instances of the same provider type.
/// </remarks>
/// <example>
/// Single instance (flat config section):
/// <code>
/// builder.Services.AddCompositeFileProvider(providers =>
/// {
///     providers.AddS3Provider(builder.Configuration, alias: "r2");
///     providers.AddLocalFileSystemProvider(builder.Configuration, alias: "local");
/// });
/// </code>
///
/// Multiple S3 instances (named subsections):
/// <code>
/// builder.Services.AddCompositeFileProvider(providers =>
/// {
///     providers.AddS3Provider(builder.Configuration, sectionName: "S3:R2",    alias: "r2");
///     providers.AddS3Provider(builder.Configuration, sectionName: "S3:MinIO", alias: "minio");
/// });
/// </code>
///
/// appsettings.json for multiple S3 instances:
/// <code>
/// {
///   "S3": {
///     "R2":    { "Endpoint": "https://...", "Region": "auto",      "AccessKey": "...", "SecretKey": "..." },
///     "MinIO": { "Endpoint": "http://...",  "Region": "us-east-1", "AccessKey": "...", "SecretKey": "..." }
///   }
/// }
/// </code>
/// </example>
public sealed class CompositeProviderBuilder
{
    private readonly List<ProviderRegistration> _registrations = new();
    private readonly HashSet<string> _aliases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILoggerFactory    _loggerFactory;
    private readonly IHttpClientFactory? _httpClientFactory;

    internal IReadOnlyList<ProviderRegistration> Registrations => _registrations;

    public CompositeProviderBuilder(
        ILoggerFactory?    loggerFactory     = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _loggerFactory     = loggerFactory     ?? NullLoggerFactory.Instance;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Adds an S3-compatible provider, binding its options from
    /// <paramref name="sectionName"/> in <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="alias">
    /// A unique alias for this provider instance. Used as a prefix on
    /// volume ID conflicts and to distinguish multiple S3 instances.
    /// </param>
    /// <param name="sectionName">
    /// The configuration section to bind. Defaults to <c>"S3"</c>.
    /// For multiple S3 instances use distinct subsections,
    /// e.g. <c>"S3:R2"</c> and <c>"S3:MinIO"</c>.
    /// </param>
    public CompositeProviderBuilder AddS3Provider(
        IConfiguration configuration,
        string alias,
        string sectionName = S3ProviderOptions.DefaultSectionName)
    {
        ValidateAlias(alias);

        var options = new S3ProviderOptions();
        configuration.GetSection(sectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException(
                $"S3 configuration is missing. Ensure the " +
                $"'{sectionName}' section has a non-empty Endpoint.");

        var signer   = new SigV4Signer(options.AccessKey, options.SecretKey, options.Region, options.UseBearerAuth);
        var http     = _httpClientFactory?.CreateClient("FileManager.S3") ?? new HttpClient();
        var logger   = _loggerFactory.CreateLogger<S3Client>();
        var client   = new S3Client(http, signer, options.Endpoint, options.VirtualHostedStyle, logger);
        var provider = new S3FileProvider(client, options);

        Register(provider, alias);
        return this;
    }

    /// <summary>
    /// Adds a local filesystem provider, binding its options from
    /// <paramref name="sectionName"/> in <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="alias">
    /// A unique alias for this provider instance.
    /// </param>
    /// <param name="sectionName">
    /// The configuration section to bind. Defaults to <c>"LocalFileSystem"</c>.
    /// For multiple instances use distinct subsections,
    /// e.g. <c>"LocalFileSystem:Docs"</c> and <c>"LocalFileSystem:Archive"</c>.
    /// </param>
    public CompositeProviderBuilder AddLocalFileSystemProvider(
        IConfiguration configuration,
        string alias,
        string sectionName = LocalFileSystemOptions.DefaultSectionName)
    {
        ValidateAlias(alias);

        var options = new LocalFileSystemOptions();
        configuration.GetSection(sectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new InvalidOperationException(
                $"LocalFileSystem configuration is missing. Ensure the " +
                $"'{sectionName}' section has a non-empty RootPath.");

        var provider = new LocalFileSystemProvider(options);
        Register(provider, alias);
        return this;
    }

    /// <summary>
    /// Adds any custom <see cref="IFileProvider"/> implementation.
    /// </summary>
    /// <param name="provider">The provider instance.</param>
    /// <param name="alias">A unique alias for this provider instance.</param>
    public CompositeProviderBuilder AddProvider(
        IFileProvider provider,
        string alias)
    {
        ValidateAlias(alias);
        Register(provider, alias);
        return this;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Alias must not be empty.", nameof(alias));

        if (!_aliases.Add(alias))
            throw new InvalidOperationException(
                $"A provider with alias '{alias}' is already registered. " +
                $"Each provider instance must have a unique alias.");
    }

    private void Register(IFileProvider provider, string alias)
    {
        _registrations.Add(new ProviderRegistration(provider, alias));
    }
}
