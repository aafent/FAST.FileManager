using FAST.FileManager.Abstractions;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace FAST.FileManager.Providers.S3;

/// <summary>
/// An <see cref="IFileProvider"/> implementation for S3-compatible backends
/// (MinIO, AWS S3, etc.). Uses a hand-rolled S3 REST client with SigV4
/// signing — no AWS SDK dependency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Folder semantics:</b> S3 has no native folders. This provider emulates
/// them using the "/" key delimiter convention:
/// <list type="bullet">
///   <item><description>
///     Folders appear as CommonPrefixes in ListObjectsV2 results.
///   </description></item>
///   <item><description>
///     CreateFolder puts a zero-byte marker object whose key ends with "/".
///   </description></item>
///   <item><description>
///     DeleteFolder, RenameFolder, and MoveFolder iterate over all objects
///     under the prefix and copy/delete them individually.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>FileReference:</b> For files the FileReference is the full S3 object
/// key (e.g. <c>documents/reports/q1.pdf</c>). For folders it is the prefix
/// string ending with "/" (e.g. <c>documents/reports/</c>).
/// </para>
/// </remarks>
public sealed class S3FileProvider : IFileProvider
{
    private readonly S3Client _client;
    private readonly S3ProviderOptions _options;

    /// <summary>
    /// Creates an <see cref="S3FileProvider"/>. Used by the DI container
    /// via <see cref="S3ProviderServiceCollectionExtensions.AddS3FileProvider"/>.
    /// </summary>
    public S3FileProvider(S3Client client, S3ProviderOptions options)
    {
        _client = client;
        _options = options;
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Capabilities GetCapabilities() => Capabilities.Full;

    // ── Volumes (S3 buckets) ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<IReadOnlyList<Volume>>> GetVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // If buckets are explicitly configured use them directly —
            // avoids calling the ListBuckets root endpoint which may not be
            // reachable in all network environments (e.g. Cloudflare R2).
            if (_options.Buckets.Count > 0)
            {
                var configured = _options.Buckets
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Select(b => new Volume(b.Trim()))
                    .ToList();
                return FileOperationResult<IReadOnlyList<Volume>>.Ok(configured);
            }

            // Fall back to ListBuckets when no buckets are configured.
            var buckets = await _client.ListBucketsAsync(cancellationToken);
            var volumes = buckets.Select(b => new Volume(b.Name)).ToList();
            return FileOperationResult<IReadOnlyList<Volume>>.Ok(volumes);
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<IReadOnlyList<Volume>>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<IReadOnlyList<Volume>>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<Volume>>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<IReadOnlyList<StorageItem>>> ListAsync(
        StructuredKey folder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = folder.VolumeId;
            var prefix = FolderPrefix(folder);

            var result = await _client.ListObjectsAsync(bucket, prefix, cancellationToken);

            var items = new List<StorageItem>();

            // Folders (CommonPrefixes)
            foreach (var folderPrefix in result.FolderPrefixes)
            {
                var folderName = FolderNameFromPrefix(folderPrefix, prefix);
                if (string.IsNullOrEmpty(folderName)) continue;

                var key = KeyFromPrefix(folder.VolumeId, folderPrefix);
                items.Add(new StorageItem(StorageItemKind.Folder, key, folderName)
                {
                    FileReference = folderPrefix,
                });
            }

            // Files (Contents)
            foreach (var obj in result.Files)
            {
                var fileName = FileNameFromKey(obj.Key, prefix);
                if (string.IsNullOrEmpty(fileName)) continue;

                var key = new StructuredKey(
                    folder.VolumeId,
                    folder.Segments.Append(fileName));

                items.Add(new StorageItem(StorageItemKind.File, key, fileName)
                {
                    Size = obj.Size,
                    LastModified = ParseDate(obj.LastModified),
                    ContentType = MimeType.Guess(fileName),
                    Owner = obj.Owner,
                    FileReference = obj.Key,
                    Tag = obj.ETag,
                });
            }

            // Sort: folders first, then files, each group alphabetically.
            var sorted = items
                .OrderBy(i => i.IsFile ? 1 : 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return FileOperationResult<IReadOnlyList<StorageItem>>.Ok(sorted);
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<IReadOnlyList<StorageItem>>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<IReadOnlyList<StorageItem>>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<StorageItem>>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── CreateFolder ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<StorageItem>> CreateFolderAsync(
        StructuredKey parent,
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = parent.VolumeId;
            var newPrefix = FolderPrefix(parent) + name + "/";

            // Conflict check: does a folder marker or any object under this
            // prefix already exist?
            if (await FolderExistsAsync(bucket, newPrefix, cancellationToken))
                return FileOperationResult<StorageItem>.Fail(
                    FileOperationError.Conflict,
                    $"A folder named '{name}' already exists.");

            await _client.PutFolderMarkerAsync(bucket, newPrefix, cancellationToken);

            var itemKey = parent.GetChild(name);
            var item = new StorageItem(StorageItemKind.Folder, itemKey, name)
            {
                FileReference = newPrefix,
            };

            return FileOperationResult<StorageItem>.Ok(item, newPrefix);
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult> DeleteAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = item.Key.VolumeId;

            if (item.IsFolder)
            {
                var prefix = (string)item.FileReference!;
                await DeletePrefixAsync(bucket, prefix, cancellationToken);
            }
            else
            {
                var key = (string)item.FileReference!;
                await _client.DeleteObjectAsync(bucket, key, cancellationToken);
            }

            return FileOperationResult.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult.Fail(MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult.Fail(FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<StorageItem>> RenameAsync(
        StorageItem item,
        string newName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = item.Key.VolumeId;
            var parentKey = item.Key.GetParent();

            if (item.IsFolder)
            {
                var oldPrefix = (string)item.FileReference!;
                var newPrefix = ParentPrefix(oldPrefix) + newName + "/";

                if (await FolderExistsAsync(bucket, newPrefix, cancellationToken))
                    return FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"A folder named '{newName}' already exists.");

                await CopyPrefixAsync(bucket, oldPrefix, newPrefix, cancellationToken);
                await DeletePrefixAsync(bucket, oldPrefix, cancellationToken);

                var newKey = parentKey.GetChild(newName);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, newName)
                {
                    FileReference = newPrefix,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newPrefix);
            }
            else
            {
                var oldKey = (string)item.FileReference!;
                var newKey = ParentPrefix(oldKey) + newName;

                if (await _client.HeadObjectAsync(bucket, newKey, cancellationToken) is not null)
                    return FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"A file named '{newName}' already exists.");

                await _client.CopyObjectAsync(bucket, oldKey, newKey, cancellationToken);
                await _client.DeleteObjectAsync(bucket, oldKey, cancellationToken);

                var newStructuredKey = parentKey.GetChild(newName);
                var newItem = new StorageItem(StorageItemKind.File, newStructuredKey, newName)
                {
                    FileReference = newKey,
                    Size = item.Size,
                    LastModified = item.LastModified,
                    ContentType = MimeType.Guess(newName),
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newKey);
            }
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<StorageItem>> MoveAsync(
        StorageItem item,
        StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = item.Key.VolumeId;
            var targetPrefix = FolderPrefix(targetFolder);

            if (item.IsFolder)
            {
                var oldPrefix = (string)item.FileReference!;
                var newPrefix = targetPrefix + item.Name + "/";

                if (await FolderExistsAsync(bucket, newPrefix, cancellationToken))
                    return FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"A folder named '{item.Name}' already exists in the target.");

                await CopyPrefixAsync(bucket, oldPrefix, newPrefix, cancellationToken);
                await DeletePrefixAsync(bucket, oldPrefix, cancellationToken);

                var newKey = targetFolder.GetChild(item.Name);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, item.Name)
                {
                    FileReference = newPrefix,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newPrefix);
            }
            else
            {
                var oldKey = (string)item.FileReference!;
                var newKey = targetPrefix + item.Name;

                if (await _client.HeadObjectAsync(bucket, newKey, cancellationToken) is not null)
                    return FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"A file named '{item.Name}' already exists in the target.");

                await _client.CopyObjectAsync(bucket, oldKey, newKey, cancellationToken);
                await _client.DeleteObjectAsync(bucket, oldKey, cancellationToken);

                var newStructuredKey = targetFolder.GetChild(item.Name);
                var newItem = new StorageItem(StorageItemKind.File, newStructuredKey, item.Name)
                {
                    FileReference = newKey,
                    Size = item.Size,
                    LastModified = item.LastModified,
                    ContentType = item.ContentType,
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newKey);
            }
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<StorageItem>> DuplicateAsync(
        StorageItem item,
        string newName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = item.Key.VolumeId;
            var parentKey = item.Key.GetParent();

            if (item.IsFolder)
            {
                var oldPrefix = (string)item.FileReference!;
                var newPrefix = ParentPrefix(oldPrefix) + newName + "/";

                await CopyPrefixAsync(bucket, oldPrefix, newPrefix, cancellationToken);

                var newKey = parentKey.GetChild(newName);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, newName)
                {
                    FileReference = newPrefix,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newPrefix);
            }
            else
            {
                var oldKey = (string)item.FileReference!;
                var newKey = ParentPrefix(oldKey) + newName;

                await _client.CopyObjectAsync(bucket, oldKey, newKey, cancellationToken);

                var newStructuredKey = parentKey.GetChild(newName);
                var newItem = new StorageItem(StorageItemKind.File, newStructuredKey, newName)
                {
                    FileReference = newKey,
                    Size = item.Size,
                    LastModified = DateTimeOffset.UtcNow,
                    ContentType = MimeType.Guess(newName),
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newKey);
            }
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<StorageItem>> CopyAsync(
        StorageItem item,
        StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = item.Key.VolumeId;
            var targetPrefix = FolderPrefix(targetFolder);

            if (item.IsFolder)
            {
                var oldPrefix = (string)item.FileReference!;
                var newPrefix = targetPrefix + item.Name + "/";

                if (await FolderExistsAsync(bucket, newPrefix, cancellationToken))
                    return FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"A folder named '{item.Name}' already exists in the target.");

                await CopyPrefixAsync(bucket, oldPrefix, newPrefix, cancellationToken);

                var newKey = targetFolder.GetChild(item.Name);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, item.Name)
                {
                    FileReference = newPrefix,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newPrefix);
            }
            else
            {
                var oldKey = (string)item.FileReference!;
                var newKey = targetPrefix + item.Name;

                if (await _client.HeadObjectAsync(bucket, newKey, cancellationToken) is not null)
                    return FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"A file named '{item.Name}' already exists in the target.");

                await _client.CopyObjectAsync(bucket, oldKey, newKey, cancellationToken);

                var newStructuredKey = targetFolder.GetChild(item.Name);
                var newItem = new StorageItem(StorageItemKind.File, newStructuredKey, item.Name)
                {
                    FileReference = newKey,
                    Size = item.Size,
                    LastModified = item.LastModified,
                    ContentType = item.ContentType,
                    Owner = item.Owner,
                    ReadOnly = item.ReadOnly,
                    Tag = item.Tag,
                    AppReference = item.AppReference,
                };
                return FileOperationResult<StorageItem>.Ok(newItem, newKey);
            }
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<StorageItem>> UploadAsync(
        StructuredKey targetFolder,
        string name,
        Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = targetFolder.VolumeId;
            var key = FolderPrefix(targetFolder) + name;

            if (await _client.HeadObjectAsync(bucket, key, cancellationToken) is not null)
                return FileOperationResult<StorageItem>.Fail(
                    FileOperationError.Conflict,
                    $"A file named '{name}' already exists.");

            var mimeType = contentType ?? MimeType.Guess(name);
            await _client.PutObjectAsync(bucket, key, content, mimeType, cancellationToken);

            var itemKey = targetFolder.GetChild(name);
            var item = new StorageItem(StorageItemKind.File, itemKey, name)
            {
                FileReference = key,
                ContentType = mimeType,
                LastModified = DateTimeOffset.UtcNow,
            };

            return FileOperationResult<StorageItem>.Ok(item, key);
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileOperationResult<Stream>> DownloadAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bucket = item.Key.VolumeId;
            var key = (string)item.FileReference!;
            var stream = await _client.GetObjectAsync(bucket, key, cancellationToken);
            return FileOperationResult<Stream>.Ok(stream, key);
        }
        catch (OperationCanceledException ex)
        {
            return FileOperationResult<Stream>.Fail(
                FileOperationError.Cancelled, "Operation was cancelled.", ex);
        }
        catch (S3Exception ex)
        {
            return FileOperationResult<Stream>.Fail(
                MapS3Error(ex), ex.Message, ex);
        }
        catch (Exception ex)
        {
            return FileOperationResult<Stream>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the S3 prefix string for a StructuredKey folder.
    /// The root returns "" (list the whole bucket); sub-folders return
    /// "segment/segment/" (always ends with /).
    /// </summary>
    private static string FolderPrefix(StructuredKey key)
    {
        if (key.IsRoot) return string.Empty;
        return string.Join('/', key.Segments) + "/";
    }

    /// <summary>
    /// Returns the parent prefix of a key or prefix string.
    /// e.g. "a/b/c" → "a/b/"   "a/b/" → "a/"   "a/" → ""
    /// </summary>
    private static string ParentPrefix(string keyOrPrefix)
    {
        var trimmed = keyOrPrefix.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : trimmed[..(lastSlash + 1)];
    }

    /// <summary>
    /// Derives a folder's display name from its prefix and its parent prefix.
    /// e.g. prefix="docs/reports/" parentPrefix="docs/" → "reports"
    /// </summary>
    private static string FolderNameFromPrefix(string prefix, string parentPrefix)
    {
        var relative = prefix[parentPrefix.Length..].TrimEnd('/');
        return relative;
    }

    /// <summary>
    /// Derives a file's display name from its key and its parent prefix.
    /// e.g. key="docs/reports/q1.pdf" parentPrefix="docs/reports/" → "q1.pdf"
    /// </summary>
    private static string FileNameFromKey(string key, string parentPrefix)
        => key[parentPrefix.Length..];

    /// <summary>
    /// Builds a StructuredKey for a folder prefix within the given volume.
    /// e.g. volumeId="my-bucket" prefix="docs/reports/" →
    ///   StructuredKey("my-bucket", ["docs","reports"])
    /// </summary>
    private static StructuredKey KeyFromPrefix(string volumeId, string prefix)
    {
        var segments = prefix
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new StructuredKey(volumeId, segments);
    }

    /// <summary>
    /// Returns true when any object exists under the given prefix.
    /// Used for conflict detection on folder operations.
    /// </summary>
    private async Task<bool> FolderExistsAsync(
        string bucket, string prefix, CancellationToken ct)
    {
        // List with max-keys=1: if anything comes back, the folder exists.
        var result = await _client.ListObjectsAsync(bucket, prefix, ct);
        return result.Files.Count > 0 || result.FolderPrefixes.Count > 0;
    }

    /// <summary>
    /// Copies all objects under <paramref name="sourcePrefix"/> to the
    /// corresponding keys under <paramref name="destPrefix"/>.
    /// </summary>
    private async Task CopyPrefixAsync(
        string bucket,
        string sourcePrefix,
        string destPrefix,
        CancellationToken ct)
    {
        var result = await _client.ListObjectsAsync(bucket, sourcePrefix, ct);

        // Copy all objects directly under this prefix.
        foreach (var obj in result.Files)
        {
            var relativeKey = obj.Key[sourcePrefix.Length..];
            var destKey = destPrefix + relativeKey;
            await _client.CopyObjectAsync(bucket, obj.Key, destKey, ct);
        }

        // Recurse into sub-folders.
        foreach (var subPrefix in result.FolderPrefixes)
        {
            var relativePrefix = subPrefix[sourcePrefix.Length..];
            var destSubPrefix = destPrefix + relativePrefix;
            await CopyPrefixAsync(bucket, subPrefix, destSubPrefix, ct);
        }

        // Create the folder marker at the destination.
        await _client.PutFolderMarkerAsync(bucket, destPrefix, ct);
    }

    /// <summary>
    /// Deletes all objects under <paramref name="prefix"/>, including the
    /// folder marker itself.
    /// </summary>
    private async Task DeletePrefixAsync(
        string bucket, string prefix, CancellationToken ct)
    {
        var result = await _client.ListObjectsAsync(bucket, prefix, ct);

        foreach (var obj in result.Files)
            await _client.DeleteObjectAsync(bucket, obj.Key, ct);

        foreach (var subPrefix in result.FolderPrefixes)
            await DeletePrefixAsync(bucket, subPrefix, ct);

        // Delete the folder marker itself (key = prefix).
        try { await _client.DeleteObjectAsync(bucket, prefix, ct); }
        catch { /* Marker may not exist; ignore */ }
    }

    /// <summary>
    /// Maps an <see cref="S3Exception"/> HTTP status code or S3 error code
    /// to a <see cref="FileOperationError"/>.
    /// </summary>
    private static FileOperationError MapS3Error(S3Exception ex) =>
        ex.S3Code switch
        {
            "NoSuchKey" or "NoSuchBucket" => FileOperationError.NotFound,
            "AccessDenied" or "InvalidAccessKeyId"
                or "SignatureDoesNotMatch" => FileOperationError.AuthenticationFailed,
            "EntityTooLarge" => FileOperationError.InvalidArgument,
            _ when ex.StatusCode == 404 => FileOperationError.NotFound,
            _ when ex.StatusCode == 403 => FileOperationError.AuthenticationFailed,
            _ when ex.StatusCode >= 500 => FileOperationError.NetworkError,
            _ => FileOperationError.Unknown
        };

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return DateTimeOffset.TryParse(value, out var dt) ? dt : null;
    }
}
