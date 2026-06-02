namespace FAST.FileManager.Providers.LocalFileSystem;

/// <summary>
/// Configuration options for the local filesystem file provider.
/// Bind from <c>appsettings.json</c> using:
/// <c>builder.Services.AddLocalFileSystemProvider(builder.Configuration)</c>
/// </summary>
/// <example>
/// <code>
/// "LocalFileSystem": {
///   "RootPath":       "C:\\FileManagerRoot",
///   "VolumeName":     "Local Storage",
///   "ReadOnly":       false,
///   "MaxUploadBytes": 104857600
/// }
/// </code>
/// </example>
public sealed class LocalFileSystemOptions
{
    /// <summary>The default configuration section name.</summary>
    public const string DefaultSectionName = "LocalFileSystem";

    /// <summary>
    /// The absolute path on the server's filesystem that acts as the root
    /// of the file manager. All operations are restricted to this directory
    /// and its descendants. Path traversal outside this root is rejected.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// The display name shown as the volume name in the file manager UI.
    /// Defaults to the last segment of <see cref="RootPath"/> when not set.
    /// </summary>
    public string? VolumeName { get; set; }

    /// <summary>
    /// When true, all write operations (create folder, rename, move, copy,
    /// duplicate, upload, delete) are disabled. The provider reports only
    /// <see cref="FAST.FileManager.Abstractions.Capabilities.CanDownload"/>.
    /// Useful for browsing log files, configuration, or read-only archives.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Optional maximum size in bytes for a single upload.
    /// Null means no application-level limit (the server's request size
    /// limit still applies). Mapped to
    /// <see cref="FAST.FileManager.Abstractions.Capabilities.MaxUploadSizeBytes"/>.
    /// Example: 104857600 = 100 MB.
    /// </summary>
    public long? MaxUploadBytes { get; set; }

    /// <summary>
    /// Returns the effective volume display name — <see cref="VolumeName"/>
    /// when set, otherwise the last path segment of <see cref="RootPath"/>.
    /// </summary>
    public string EffectiveVolumeName =>
        !string.IsNullOrWhiteSpace(VolumeName)
            ? VolumeName!
            : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
              ?? RootPath;
}
