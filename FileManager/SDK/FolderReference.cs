using FAST.FileManager.Abstractions;

namespace FAST.FileManager.SDK;

/// <summary>
/// A developer-facing address for a folder in a storage volume.
/// Constructed from three parts (volume, path, folder name) and used
/// directly in <see cref="FileManagerClient"/> operations.
/// </summary>
/// <remarks>
/// Obtain a <see cref="FolderReference"/> either by constructing one directly
/// from the three address parts, or by calling
/// <see cref="StorageCatalog.GetFolderReferenceAsync"/> which returns a
/// reference pre-populated with the provider-native identity needed for
/// efficient server-side operations.
/// </remarks>
public sealed class FolderReference
{
    /// <summary>
    /// Creates a folder reference from its three address parts.
    /// </summary>
    /// <param name="volume">
    /// The volume (bucket) name. For S3 / R2 this is the bucket name.
    /// </param>
    /// <param name="path">
    /// The path within the volume to the folder's parent, using "/" as separator.
    /// Use an empty string or null for folders at the volume root.
    /// Example: <c>"documents"</c>
    /// </param>
    /// <param name="folderName">
    /// The folder name. Example: <c>"reports"</c>
    /// </param>
    public FolderReference(string volume, string? path, string folderName)
    {
        if (string.IsNullOrWhiteSpace(volume))
            throw new ArgumentException("Volume must not be empty.", nameof(volume));
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name must not be empty.", nameof(folderName));

        Volume     = volume;
        Path       = NormalizePath(path);
        FolderName = folderName;
    }

    /// <summary>Internal constructor — preserves provider-native StorageItem.</summary>
    internal FolderReference(string volume, string? path, string folderName, StorageItem item)
        : this(volume, path, folderName)
    {
        Item = item;
    }

    /// <summary>The volume (bucket) name.</summary>
    public string Volume { get; }

    /// <summary>
    /// The path to the folder's parent within the volume.
    /// Empty string for folders at the volume root.
    /// </summary>
    public string Path { get; }

    /// <summary>The folder name.</summary>
    public string FolderName { get; }

    /// <summary>
    /// The provider-native <see cref="StorageItem"/>, populated when this
    /// reference was obtained from a <see cref="StorageCatalog"/>.
    /// Null when constructed directly from address parts.
    /// </summary>
    internal StorageItem? Item { get; }

    /// <summary>
    /// Builds the <see cref="StructuredKey"/> for this folder.
    /// </summary>
    internal StructuredKey ToKey()
    {
        var segments = PathSegments().Append(FolderName);
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
            ? $"{Volume}/{FolderName}"
            : $"{Volume}/{Path}/{FolderName}";
}
