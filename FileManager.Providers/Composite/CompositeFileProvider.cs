using FAST.FileManager.Abstractions;

namespace FAST.FileManager.Providers.Composite;

/// <summary>
/// An <see cref="IFileProvider"/> that aggregates multiple underlying
/// providers into a single unified view. Volumes from all providers appear
/// together in the left panel. Operations are routed to the correct
/// provider based on the volume ID.
/// </summary>
/// <remarks>
/// <para>
/// <b>Volume ID conflict resolution:</b> when two providers expose a volume
/// with the same ID, the first registered provider keeps the original ID.
/// Subsequent providers with the same volume ID have their volumes prefixed
/// with their alias: <c>"{alias}::{volumeId}"</c>.
/// </para>
/// <para>
/// <b>Cross-provider operations:</b> Move and Copy between volumes belonging
/// to different providers are handled as Download + Upload pairs transparently.
/// </para>
/// </remarks>
public sealed class CompositeFileProvider : IFileProvider
{
    // Maps resolved volume ID → (underlying provider, original volume ID)
    private readonly Dictionary<string, (IFileProvider Provider, string OriginalVolumeId)>
        _volumeMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Volume> _volumes = new();
    private bool _initialised;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private readonly IReadOnlyList<ProviderRegistration> _registrations;

    public CompositeFileProvider(IEnumerable<ProviderRegistration> registrations)
    {
        _registrations = registrations.ToList();
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the union of all providers' capabilities — an operation is
    /// available if at least one provider supports it.
    /// </summary>
    public Capabilities GetCapabilities()
    {
        var all = _registrations.Select(r => r.Provider.GetCapabilities()).ToList();
        return new Capabilities
        {
            CanCreateFolder    = all.Any(c => c.CanCreateFolder),
            CanDelete          = all.Any(c => c.CanDelete),
            CanRename          = all.Any(c => c.CanRename),
            CanMove            = all.Any(c => c.CanMove),
            CanCopy            = all.Any(c => c.CanCopy),
            CanUpload          = all.Any(c => c.CanUpload),
            CanDownload        = all.Any(c => c.CanDownload),
            MaxUploadSizeBytes = all
                .Where(c => c.MaxUploadSizeBytes.HasValue)
                .Select(c => c.MaxUploadSizeBytes!.Value)
                .DefaultIfEmpty(0)
                .Max() is > 0 and var max ? max : null,
        };
    }

    // ── Volumes ───────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<IReadOnlyList<Volume>>> GetVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitialisedAsync(cancellationToken);
            return FileOperationResult<IReadOnlyList<Volume>>.Ok(_volumes);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<Volume>>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<IReadOnlyList<StorageItem>>> ListAsync(
        StructuredKey folder,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedKey) = await ResolveAsync(folder, cancellationToken);
        if (provider is null)
            return NotFound<IReadOnlyList<StorageItem>>(folder.VolumeId);

        var result = await provider.ListAsync(translatedKey, cancellationToken);
        return result.Success
            ? FileOperationResult<IReadOnlyList<StorageItem>>.Ok(
                TranslateItems(result.Value!, folder.VolumeId, translatedKey.VolumeId),
                result.ResultReference)
            : Fail<IReadOnlyList<StorageItem>>(result);
    }

    // ── CreateFolder ──────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> CreateFolderAsync(
        StructuredKey parent, string name,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedKey) = await ResolveAsync(parent, cancellationToken);
        if (provider is null) return NotFound<StorageItem>(parent.VolumeId);

        var result = await provider.CreateFolderAsync(translatedKey, name, cancellationToken);
        return result.Success
            ? Ok(result.Value!, parent.VolumeId, translatedKey.VolumeId, result.ResultReference)
            : Fail<StorageItem>(result);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult> DeleteAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedItem) = await ResolveItemAsync(item, cancellationToken);
        if (provider is null) return NotFound(item.Key.VolumeId);
        return await provider.DeleteAsync(translatedItem, cancellationToken);
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> RenameAsync(
        StorageItem item, string newName,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedItem) = await ResolveItemAsync(item, cancellationToken);
        if (provider is null) return NotFound<StorageItem>(item.Key.VolumeId);

        var result = await provider.RenameAsync(translatedItem, newName, cancellationToken);
        return result.Success
            ? Ok(result.Value!, item.Key.VolumeId, translatedItem.Key.VolumeId, result.ResultReference)
            : Fail<StorageItem>(result);
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> MoveAsync(
        StorageItem item, StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        var (srcProvider, translatedItem)   = await ResolveItemAsync(item, cancellationToken);
        var (tgtProvider, translatedTarget) = await ResolveAsync(targetFolder, cancellationToken);

        if (srcProvider is null) return NotFound<StorageItem>(item.Key.VolumeId);
        if (tgtProvider is null) return NotFound<StorageItem>(targetFolder.VolumeId);

        // Same provider — delegate directly.
        if (ReferenceEquals(srcProvider, tgtProvider))
        {
            var result = await srcProvider.MoveAsync(
                translatedItem, translatedTarget, cancellationToken);
            return result.Success
                ? Ok(result.Value!, targetFolder.VolumeId, translatedTarget.VolumeId,
                     result.ResultReference)
                : Fail<StorageItem>(result);
        }

        // Cross-provider — Download + Upload + Delete.
        return await CrossProviderCopyOrMoveAsync(
            srcProvider, tgtProvider,
            translatedItem, translatedTarget,
            item.Key.VolumeId, targetFolder.VolumeId,
            deleteSource: true, cancellationToken);
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> CopyAsync(
        StorageItem item, StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        var (srcProvider, translatedItem)   = await ResolveItemAsync(item, cancellationToken);
        var (tgtProvider, translatedTarget) = await ResolveAsync(targetFolder, cancellationToken);

        if (srcProvider is null) return NotFound<StorageItem>(item.Key.VolumeId);
        if (tgtProvider is null) return NotFound<StorageItem>(targetFolder.VolumeId);

        if (ReferenceEquals(srcProvider, tgtProvider))
        {
            var result = await srcProvider.CopyAsync(
                translatedItem, translatedTarget, cancellationToken);
            return result.Success
                ? Ok(result.Value!, targetFolder.VolumeId, translatedTarget.VolumeId,
                     result.ResultReference)
                : Fail<StorageItem>(result);
        }

        return await CrossProviderCopyOrMoveAsync(
            srcProvider, tgtProvider,
            translatedItem, translatedTarget,
            item.Key.VolumeId, targetFolder.VolumeId,
            deleteSource: false, cancellationToken);
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> DuplicateAsync(
        StorageItem item, string newName,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedItem) = await ResolveItemAsync(item, cancellationToken);
        if (provider is null) return NotFound<StorageItem>(item.Key.VolumeId);

        var result = await provider.DuplicateAsync(translatedItem, newName, cancellationToken);
        return result.Success
            ? Ok(result.Value!, item.Key.VolumeId, translatedItem.Key.VolumeId,
                 result.ResultReference)
            : Fail<StorageItem>(result);
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> UploadAsync(
        StructuredKey targetFolder, string name, Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedKey) = await ResolveAsync(targetFolder, cancellationToken);
        if (provider is null) return NotFound<StorageItem>(targetFolder.VolumeId);

        var result = await provider.UploadAsync(
            translatedKey, name, content, contentType, cancellationToken);
        return result.Success
            ? Ok(result.Value!, targetFolder.VolumeId, translatedKey.VolumeId,
                 result.ResultReference)
            : Fail<StorageItem>(result);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<Stream>> DownloadAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        var (provider, translatedItem) = await ResolveItemAsync(item, cancellationToken);
        if (provider is null) return NotFound<Stream>(item.Key.VolumeId);
        return await provider.DownloadAsync(translatedItem, cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE — INITIALISATION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads volumes from all providers, resolves conflicts, and builds
    /// the volume map. Runs once lazily on first access.
    /// </summary>
    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reg in _registrations)
            {
                var volResult = await reg.Provider.GetVolumesAsync(ct);
                if (volResult.Failed) continue;

                foreach (var volume in volResult.Value!)
                {
                    var resolvedId = volume.Id;

                    // Conflict: prefix with alias.
                    if (seenIds.Contains(resolvedId))
                        resolvedId = $"{reg.Alias}::{volume.Id}";

                    // If still conflicts (unlikely but defensive), keep appending.
                    var attempt = resolvedId;
                    var counter = 2;
                    while (seenIds.Contains(attempt))
                        attempt = $"{resolvedId}_{counter++}";
                    resolvedId = attempt;

                    seenIds.Add(resolvedId);
                    _volumeMap[resolvedId] = (reg.Provider, volume.Id);

                    var displayName = resolvedId == volume.Id
                        ? volume.DisplayName
                        : $"{volume.DisplayName} ({reg.Alias})";

                    _volumes.Add(new Volume(resolvedId, displayName));
                }
            }

            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE — ROUTING
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a composite <see cref="StructuredKey"/> to its underlying
    /// provider and the translated key (with the original volume ID).
    /// </summary>
    private async Task<(IFileProvider? Provider, StructuredKey TranslatedKey)>
        ResolveAsync(StructuredKey key, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);

        if (!_volumeMap.TryGetValue(key.VolumeId, out var entry))
            return (null, default);

        var translatedKey = new StructuredKey(entry.OriginalVolumeId, key.Segments);
        return (entry.Provider, translatedKey);
    }

    /// <summary>
    /// Resolves a <see cref="StorageItem"/> to its underlying provider and
    /// a translated item (with the original volume ID in the key).
    /// </summary>
    private async Task<(IFileProvider? Provider, StorageItem TranslatedItem)>
        ResolveItemAsync(StorageItem item, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);

        if (!_volumeMap.TryGetValue(item.Key.VolumeId, out var entry))
            return (null, item);

        var translatedKey  = new StructuredKey(entry.OriginalVolumeId, item.Key.Segments);
        var translatedItem = new StorageItem(item.Kind, translatedKey, item.Name)
        {
            Size          = item.Size,
            LastModified  = item.LastModified,
            ContentType   = item.ContentType,
            Owner         = item.Owner,
            ReadOnly      = item.ReadOnly,
            Tag           = item.Tag,
            AppReference  = item.AppReference,
            FileReference = item.FileReference,
        };
        return (entry.Provider, translatedItem);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE — CROSS-PROVIDER OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<FileOperationResult<StorageItem>> CrossProviderCopyOrMoveAsync(
        IFileProvider srcProvider,
        IFileProvider tgtProvider,
        StorageItem translatedItem,
        StructuredKey translatedTarget,
        string compositeVolumeId,
        string compositeTargetVolumeId,
        bool deleteSource,
        CancellationToken ct)
    {
        // Download from source.
        var downloadResult = await srcProvider.DownloadAsync(translatedItem, ct);
        if (downloadResult.Failed)
            return Fail<StorageItem>(downloadResult);

        // Upload to target.
        await using var stream = downloadResult.Value!;
        var uploadResult = await tgtProvider.UploadAsync(
            translatedTarget,
            translatedItem.Name,
            stream,
            translatedItem.ContentType,
            ct);

        if (uploadResult.Failed)
            return Fail<StorageItem>(uploadResult);

        // Delete source if moving.
        if (deleteSource)
            await srcProvider.DeleteAsync(translatedItem, ct);

        return Ok(uploadResult.Value!, compositeTargetVolumeId,
            translatedTarget.VolumeId, uploadResult.ResultReference);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE — KEY / ITEM TRANSLATION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Translates a list of items from provider-native volume IDs back to
    /// composite volume IDs.
    /// </summary>
    private IReadOnlyList<StorageItem> TranslateItems(
        IReadOnlyList<StorageItem> items,
        string compositeVolumeId,
        string originalVolumeId)
    {
        if (string.Equals(compositeVolumeId, originalVolumeId,
            StringComparison.OrdinalIgnoreCase))
            return items;

        return items.Select(item =>
        {
            var newKey = new StructuredKey(compositeVolumeId, item.Key.Segments);
            return new StorageItem(item.Kind, newKey, item.Name)
            {
                Size          = item.Size,
                LastModified  = item.LastModified,
                ContentType   = item.ContentType,
                Owner         = item.Owner,
                ReadOnly      = item.ReadOnly,
                Tag           = item.Tag,
                AppReference  = item.AppReference,
                FileReference = item.FileReference,
            };
        }).ToList();
    }

    private static StorageItem TranslateItem(
        StorageItem item, string compositeVolumeId, string originalVolumeId)
    {
        if (string.Equals(compositeVolumeId, originalVolumeId,
            StringComparison.OrdinalIgnoreCase))
            return item;

        var newKey = new StructuredKey(compositeVolumeId, item.Key.Segments);
        return new StorageItem(item.Kind, newKey, item.Name)
        {
            Size          = item.Size,
            LastModified  = item.LastModified,
            ContentType   = item.ContentType,
            Owner         = item.Owner,
            ReadOnly      = item.ReadOnly,
            Tag           = item.Tag,
            AppReference  = item.AppReference,
            FileReference = item.FileReference,
        };
    }

    private static FileOperationResult<StorageItem> Ok(
        StorageItem item,
        string compositeVolumeId,
        string originalVolumeId,
        object? resultRef) =>
        FileOperationResult<StorageItem>.Ok(
            TranslateItem(item, compositeVolumeId, originalVolumeId),
            resultRef);

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE — RESULT HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static FileOperationResult<T> NotFound<T>(string volumeId) =>
        FileOperationResult<T>.Fail(FileOperationError.NotFound,
            $"No provider found for volume '{volumeId}'.");

    private static FileOperationResult NotFound(string volumeId) =>
        FileOperationResult.Fail(FileOperationError.NotFound,
            $"No provider found for volume '{volumeId}'.");

    private static FileOperationResult<T> Fail<T>(FileOperationResult source) =>
        FileOperationResult<T>.Fail(source.Error, source.Message, source.Exception);

    private static FileOperationResult<T> Fail<T>(FileOperationResult<T> source) =>
        FileOperationResult<T>.Fail(source.Error, source.Message, source.Exception);
}
