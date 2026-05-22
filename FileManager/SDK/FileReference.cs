using FAST.FileManager.Abstractions;

namespace FAST.FileManager.SDK;

/// <summary>
/// A developer-facing address for a file in a storage volume.
/// Constructed from three parts (volume, path, file name) and used
/// directly in <see cref="FileManagerClient"/> operations.
/// </summary>
/// <remarks>
/// Obtain a <see cref="FileReference"/> either by constructing one directly
/// from the three address parts, or by calling
/// <see cref="StorageCatalog.GetFileReferenceAsync"/> which returns a
/// reference pre-populated with the provider-native identity needed for
/// efficient server-side operations.
/// </remarks>
public sealed class FileReference
{
    /// <summary>
    /// Creates a file reference from its three address parts.
    /// </summary>
    /// <param name="volume">
    /// The volume (bucket) name. For S3 / R2 this is the bucket name.
    /// </param>
    /// <param name="path">
    /// The path within the volume, using "/" as separator.
    /// Use an empty string or null for the volume root.
    /// Example: <c>"documents/reports"</c>
    /// </param>
    /// <param name="fileName">
    /// The file name including extension. Example: <c>"q1.pdf"</c>
    /// </param>
    public FileReference(string volume, string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(volume))
            throw new ArgumentException("Volume must not be empty.", nameof(volume));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name must not be empty.", nameof(fileName));

        Volume   = volume;
        Path     = NormalizePath(path);
        FileName = fileName;
    }

    /// <summary>Internal constructor — preserves provider-native StorageItem.</summary>
    internal FileReference(string volume, string? path, string fileName, StorageItem item)
        : this(volume, path, fileName)
    {
        Item = item;
    }

    /// <summary>The volume (bucket) name.</summary>
    public string Volume { get; }

    /// <summary>
    /// The path within the volume, normalized with "/" separators.
    /// Empty string for files at the volume root.
    /// </summary>
    public string Path { get; }

    /// <summary>The file name including extension.</summary>
    public string FileName { get; }

    /// <summary>
    /// The provider-native <see cref="StorageItem"/>, populated when this
    /// reference was obtained from a <see cref="StorageCatalog"/>.
    /// Null when constructed directly from address parts.
    /// </summary>
    internal StorageItem? Item { get; }

    /// <summary>
    /// Builds the <see cref="StructuredKey"/> for this reference.
    /// </summary>
    internal StructuredKey ToKey()
    {
        var segments = PathSegments().Append(FileName);
        return new StructuredKey(Volume, segments);
    }

    /// <summary>
    /// Builds the <see cref="StructuredKey"/> for the parent folder.
    /// </summary>
    internal StructuredKey ToParentKey()
        => new(Volume, PathSegments());

    internal IEnumerable<string> PathSegments()
        => string.IsNullOrEmpty(Path)
            ? Enumerable.Empty<string>()
            : Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return string.Join('/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0));
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.IsNullOrEmpty(Path)
            ? $"{Volume}/{FileName}"
            : $"{Volume}/{Path}/{FileName}";
}
