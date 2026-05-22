namespace FAST.FileManager.Abstractions;

/// <summary>
/// Declares which operations an <see cref="IFileProvider"/> supports, so the
/// component can hide or disable unsupported actions. Implemented as a class
/// (rather than a flags enum) so non-boolean metadata can be added later
/// without a breaking change.
/// </summary>
public sealed class Capabilities
{
    /// <summary>True when the provider can create folders.</summary>
    public bool CanCreateFolder { get; init; }

    /// <summary>True when the provider can delete files and folders.</summary>
    public bool CanDelete { get; init; }

    /// <summary>True when the provider can rename files and folders.</summary>
    public bool CanRename { get; init; }

    /// <summary>True when the provider can move files and folders.</summary>
    public bool CanMove { get; init; }

    /// <summary>True when the provider can copy files and folders.</summary>
    public bool CanCopy { get; init; }

    /// <summary>True when the provider can upload files.</summary>
    public bool CanUpload { get; init; }

    /// <summary>True when the provider can download files.</summary>
    public bool CanDownload { get; init; }

    /// <summary>
    /// An optional upper bound, in bytes, on the size of a single upload.
    /// Null means the provider declares no limit. Reserved for future use by
    /// the component to validate uploads before starting them.
    /// </summary>
    public long? MaxUploadSizeBytes { get; init; }

    /// <summary>
    /// A convenience instance with every operation enabled and no upload limit.
    /// </summary>
    public static Capabilities Full { get; } = new()
    {
        CanCreateFolder = true,
        CanDelete = true,
        CanRename = true,
        CanMove = true,
        CanCopy = true,
        CanUpload = true,
        CanDownload = true,
    };

    /// <summary>
    /// A convenience instance with only the read operations
    /// (<see cref="CanDownload"/>) enabled.
    /// </summary>
    public static Capabilities ReadOnly { get; } = new()
    {
        CanDownload = true,
    };
}
