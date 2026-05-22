using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FAST.FileManager.Abstractions;

/// <summary>
/// Addresses a location within a provider: a volume identifier plus an ordered
/// list of path segments. This is the abstraction's navigation and display
/// addressing, and is also how not-yet-created items (a new folder, an upload
/// target) are addressed before they have a provider-native
/// <see cref="StorageItem.FileReference"/>.
/// </summary>
/// <remarks>
/// A <see cref="StructuredKey"/> is transport-agnostic. Each provider is
/// responsible for translating it to and from its own native addressing
/// (for S3, a flat object key). Segments never contain the separator
/// character and never include synthetic "." or ".." entries.
/// </remarks>
public readonly struct StructuredKey : IEquatable<StructuredKey>
{
    /// <summary>The separator used in the normalized string form.</summary>
    public const char Separator = '/';

    private readonly ImmutableArray<string> _segments;

    /// <summary>
    /// Creates a key for a location within the given volume.
    /// </summary>
    /// <param name="volumeId">
    /// The identifier of the volume. Must not be null or whitespace.
    /// </param>
    /// <param name="segments">
    /// The ordered path segments from the volume root to the location.
    /// An empty sequence addresses the volume root itself.
    /// </param>
    public StructuredKey(string volumeId, IEnumerable<string>? segments = null)
    {
        if (string.IsNullOrWhiteSpace(volumeId))
            throw new ArgumentException("Volume id must not be empty.", nameof(volumeId));

        VolumeId = volumeId;
        _segments = Normalize(segments);
    }

    /// <summary>The identifier of the volume this key belongs to.</summary>
    public string VolumeId { get; }

    /// <summary>
    /// The ordered path segments from the volume root to the addressed
    /// location. Empty when the key addresses the volume root.
    /// </summary>
    public ImmutableArray<string> Segments =>
        _segments.IsDefault ? ImmutableArray<string>.Empty : _segments;

    /// <summary>True when this key addresses the root of its volume.</summary>
    public bool IsRoot => Segments.Length == 0;

    /// <summary>
    /// The last path segment (the item's own name), or an empty string when
    /// this key addresses the volume root.
    /// </summary>
    public string Name => Segments.Length == 0 ? string.Empty : Segments[^1];

    /// <summary>
    /// Returns the key of the parent location, or this key unchanged when it
    /// is already the volume root.
    /// </summary>
    public StructuredKey GetParent()
    {
        if (IsRoot)
            return this;

        return new StructuredKey(VolumeId, Segments.Take(Segments.Length - 1));
    }

    /// <summary>
    /// Returns a new key addressing a child of this location.
    /// </summary>
    /// <param name="childName">The name of the child segment to append.</param>
    public StructuredKey GetChild(string childName)
    {
        if (string.IsNullOrWhiteSpace(childName))
            throw new ArgumentException("Child name must not be empty.", nameof(childName));

        return new StructuredKey(VolumeId, Segments.Append(childName));
    }

    /// <summary>
    /// The path portion of the key in normalized string form, using
    /// <see cref="Separator"/> between segments. Empty for the volume root.
    /// Does not include the volume id.
    /// </summary>
    public string Path => string.Join(Separator, Segments);

    private static ImmutableArray<string> Normalize(IEnumerable<string>? segments)
    {
        if (segments is null)
            return ImmutableArray<string>.Empty;

        var cleaned = segments
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => s.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .ToImmutableArray();

        return cleaned;
    }

    /// <inheritdoc />
    public bool Equals(StructuredKey other)
    {
        if (!string.Equals(VolumeId, other.VolumeId, StringComparison.Ordinal))
            return false;

        return Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(VolumeId, StringComparer.Ordinal);
        foreach (var segment in Segments)
            hash.Add(segment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StructuredKey left, StructuredKey right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StructuredKey left, StructuredKey right) => !left.Equals(right);

    /// <summary>
    /// Returns a display string of the form "volumeId/segment/segment".
    /// </summary>
    public override string ToString() =>
        IsRoot ? VolumeId : $"{VolumeId}{Separator}{Path}";
}
