using System.IO;

namespace FAST.FileManager.Abstractions;

/// <summary>
/// A file or a folder, as presented by an <see cref="IFileProvider"/>.
/// Files and folders share this single model; for folders, provider-specific
/// fields may be left empty.
/// </summary>
/// <remarks>
/// <para>
/// The component navigates and displays items by their <see cref="Key"/>.
/// When invoking an operation on an existing item, the component hands the
/// whole <see cref="StorageItem"/> to the provider, which uses its own
/// <see cref="FileReference"/>, <see cref="Tag"/>, and
/// <see cref="AppReference"/> to carry out the request.
/// </para>
/// <para>
/// <see cref="Tag"/>, <see cref="AppReference"/>, and <see cref="FileReference"/>
/// are provider-owned. The component treats them as opaque: it preserves them
/// and returns them unchanged to the provider on every operation.
/// </para>
/// </remarks>
public sealed class StorageItem
{
    /// <summary>
    /// Creates a storage item.
    /// </summary>
    /// <param name="kind">Whether this item is a file or a folder.</param>
    /// <param name="key">
    /// The structured key addressing this item within its volume.
    /// </param>
    /// <param name="name">
    /// The item's name, including extension for files.
    /// </param>
    public StorageItem(StorageItemKind kind, StructuredKey key, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        Kind = kind;
        Key = key;
        Name = name;
    }

    /// <summary>Whether this item is a file or a folder.</summary>
    public StorageItemKind Kind { get; }

    /// <summary>True when this item is a folder.</summary>
    public bool IsFolder => Kind == StorageItemKind.Folder;

    /// <summary>True when this item is a file.</summary>
    public bool IsFile => Kind == StorageItemKind.File;

    /// <summary>
    /// The structured key addressing this item within its volume.
    /// </summary>
    public StructuredKey Key { get; }

    /// <summary>The item's name, including extension for files.</summary>
    public string Name { get; }

    /// <summary>
    /// The friendly name: for a file, the name without its extension;
    /// for a folder, the same as <see cref="Name"/>.
    /// This value is derived and read-only.
    /// </summary>
    public string Title =>
        IsFolder ? Name : System.IO.Path.GetFileNameWithoutExtension(Name);

    /// <summary>
    /// The file extension including the leading dot (for example ".txt"),
    /// or an empty string for folders and extensionless files.
    /// </summary>
    public string Extension =>
        IsFolder ? string.Empty : System.IO.Path.GetExtension(Name);

    /// <summary>The size of the file in bytes. Zero or unset for folders.</summary>
    public long Size { get; init; }

    /// <summary>
    /// The last-modified timestamp, if the provider supplies one.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// The MIME content type, if known. Empty or null for folders and when
    /// the provider does not supply one.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The item owner, if the provider supplies one.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>
    /// True when the item must not be modified or deleted.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Provider-owned, opaque state. The component never interprets this;
    /// it preserves the value and returns it to the provider on operations.
    /// May be null.
    /// </summary>
    public object? Tag { get; init; }

    /// <summary>
    /// Provider-owned, opaque application reference. The component never
    /// interprets this; it round-trips the value back to the provider.
    /// May be null.
    /// </summary>
    public object? AppReference { get; init; }

    /// <summary>
    /// The provider's native handle to this item — the "real" reference the
    /// provider uses to act on it. For S3 this is the full object key.
    /// Opaque to the component. May be null for items that do not yet exist.
    /// </summary>
    public object? FileReference { get; init; }
}
