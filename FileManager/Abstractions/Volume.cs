namespace FAST.FileManager.Abstractions;

/// <summary>
/// A neutral top-level storage container. Each provider maps this onto a
/// familiar concept of its own: an S3 bucket, a FAST volume, a Drive account,
/// and so on. The component lists volumes in its left-hand panel.
/// </summary>
public sealed class Volume
{
    /// <summary>
    /// Creates a volume.
    /// </summary>
    /// <param name="id">
    /// The stable identifier of the volume, used as
    /// <see cref="StructuredKey.VolumeId"/>. For S3 this is the bucket name.
    /// </param>
    /// <param name="displayName">
    /// The human-readable name shown in the UI. Defaults to <paramref name="id"/>.
    /// </param>
    public Volume(string id, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Volume id must not be empty.", nameof(id));

        Id = id;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName!;
    }

    /// <summary>
    /// The stable identifier of the volume. Used to build
    /// <see cref="StructuredKey"/> instances. For S3 this is the bucket name.
    /// </summary>
    public string Id { get; }

    /// <summary>The human-readable name shown in the UI.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Optional provider-owned, opaque state associated with the volume.
    /// The component never interprets this; it is preserved and handed back
    /// to the provider. May be null.
    /// </summary>
    public object? Tag { get; init; }

    /// <summary>
    /// The structured key addressing the root of this volume.
    /// </summary>
    public StructuredKey RootKey => new(Id);
}
