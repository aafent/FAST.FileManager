using FAST.FileManager.Providers.Composite;
using FAST.FileManager.Providers.LocalFileSystem;
using FAST.FileManager.Providers.S3;

namespace FAST.FileManager.Api;

/// <summary>
/// Reads <c>appsettings.json</c> once and auto-registers all configured
/// S3 and LocalFileSystem providers into a <see cref="CompositeProviderBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>S3 — single instance</b> (section has an <c>Endpoint</c> directly):
/// <code>
/// "S3": { "Endpoint": "https://...", "AccessKey": "...", "SecretKey": "..." }
/// </code>
/// Registered with alias <c>"s3"</c>.
/// </para>
/// <para>
/// <b>S3 — multiple instances</b> (named subsections, each with an <c>Endpoint</c>):
/// <code>
/// "S3": {
///   "R2Primary":   { "Endpoint": "https://ACCOUNT1...", ... },
///   "R2Secondary": { "Endpoint": "https://ACCOUNT2...", ... }
/// }
/// </code>
/// Each subsection key becomes the alias (lowercased), e.g. <c>"r2primary"</c>.
/// </para>
/// <para>
/// The same single/multi pattern applies to <c>"LocalFileSystem"</c>.
/// </para>
/// </remarks>
internal static class ProviderConfigurationHelper
{
    /// <summary>
    /// Registers all S3 and LocalFileSystem providers found in configuration.
    /// </summary>
    public static void RegisterProviders(
        CompositeProviderBuilder builder,
        IConfiguration configuration)
    {
        RegisterS3Providers(builder, configuration);
        RegisterLocalFileSystemProviders(builder, configuration);
    }

    // ── S3 ────────────────────────────────────────────────────────────────────

    private static void RegisterS3Providers(
        CompositeProviderBuilder builder,
        IConfiguration configuration)
    {
        var s3Section = configuration.GetSection(S3ProviderOptions.DefaultSectionName);
        if (!s3Section.Exists()) return;

        // Single instance: section has "Endpoint" directly.
        if (!string.IsNullOrWhiteSpace(s3Section["Endpoint"]))
        {
            builder.AddS3Provider(configuration,
                alias: "s3",
                sectionName: S3ProviderOptions.DefaultSectionName);
            return;
        }

        // Multiple instances: each child section has its own "Endpoint".
        foreach (var child in s3Section.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child["Endpoint"])) continue;

            var alias       = child.Key.ToLowerInvariant();
            var sectionName = $"{S3ProviderOptions.DefaultSectionName}:{child.Key}";
            builder.AddS3Provider(configuration, alias: alias, sectionName: sectionName);
        }
    }

    // ── LocalFileSystem ───────────────────────────────────────────────────────

    private static void RegisterLocalFileSystemProviders(
        CompositeProviderBuilder builder,
        IConfiguration configuration)
    {
        var lfsSection = configuration.GetSection(LocalFileSystemOptions.DefaultSectionName);
        if (!lfsSection.Exists()) return;

        // Single instance: section has "RootPath" directly.
        if (!string.IsNullOrWhiteSpace(lfsSection["RootPath"]))
        {
            builder.AddLocalFileSystemProvider(configuration,
                alias: "localfilesystem",
                sectionName: LocalFileSystemOptions.DefaultSectionName);
            return;
        }

        // Multiple instances: each child section has its own "RootPath".
        foreach (var child in lfsSection.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child["RootPath"])) continue;

            var alias       = child.Key.ToLowerInvariant();
            var sectionName = $"{LocalFileSystemOptions.DefaultSectionName}:{child.Key}";
            builder.AddLocalFileSystemProvider(configuration, alias: alias, sectionName: sectionName);
        }
    }
}
