using FAST.FileManager.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FAST.FileManager.Providers.S3;

/// <summary>
/// Extension methods for registering the S3 file provider with the
/// dependency injection container.
/// </summary>
public static class S3ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the S3-compatible file provider and binds its options from
    /// the <c>"S3"</c> section of <paramref name="configuration"/>.
    /// </summary>
    /// <example>
    /// In <c>Program.cs</c> of your Blazor WASM application:
    /// <code>
    /// builder.Services.AddS3FileProvider(builder.Configuration);
    /// </code>
    /// And in <c>appsettings.json</c>:
    /// <code>
    /// {
    ///   "S3": {
    ///     "Endpoint":  "http://localhost:9000",
    ///     "Region":    "us-east-1",
    ///     "AccessKey": "minioadmin",
    ///     "SecretKey": "minioadmin"
    ///   }
    /// }
    /// </code>
    /// Then pass the provider to the component:
    /// <code>
    /// @inject IFileProvider FileProvider
    /// &lt;FileManager Provider="FileProvider" /&gt;
    /// </code>
    /// </example>
    public static IServiceCollection AddS3FileProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = S3ProviderOptions.DefaultSectionName)
    {
        // Bind and validate options.
        var options = new S3ProviderOptions();
        configuration.GetSection(sectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException(
                $"S3 configuration is missing or incomplete. " +
                $"Ensure the '{sectionName}' section in " +
                $"appsettings.json has a non-empty Endpoint value.");

        // Register a named HttpClient for the S3 provider.
        services.AddHttpClient("FileManager.S3", client =>
        {
            client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        });

        // Register options, signer, client, and provider.
        // S3Client and SigV4Signer are internal; we use factory lambdas so the
        // DI container never needs to reflect on their constructors.
        services.AddSingleton(options);

        var signer = new SigV4Signer(
            options.AccessKey,
            options.SecretKey,
            options.Region);

        services.AddScoped<IFileProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("FileManager.S3");
            var client = new S3Client(http, signer, options.Endpoint, options.VirtualHostedStyle);
            return new S3FileProvider(client, options);
        });

        return services;
    }
}
