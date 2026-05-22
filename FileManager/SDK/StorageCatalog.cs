using FAST.FileManager.Abstractions;

namespace FAST.FileManager.SDK;

/// <summary>
/// Represents the contents of a specific folder within a storage volume.
/// The listing is loaded lazily on first access and cached for subsequent calls.
/// Use <see cref="Refresh"/> to invalidate the cache for a lazy re-fetch, or
/// <see cref="RefreshAsync"/> to invalidate and immediately reload.
/// </summary>
/// <example>
/// <code>
/// var catalog = new StorageCatalog(provider, "my-bucket", "documents/reports");
///
/// if (await catalog.FileExistsAsync("q1.pdf"))
/// {
///     var fileRef = await catalog.GetFileReferenceAsync("q1.pdf");
///     // use fileRef with FileManagerClient
/// }
/// </code>
/// </example>
public sealed class StorageCatalog
{
    private readonly IFileProvider _provider;
    private readonly StructuredKey _folderKey;
    private readonly string _volume;
    private readonly string _path;

    private IReadOnlyList<StorageItem>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Creates a catalog for the specified folder.
    /// No API call is made until the first access.
    /// </summary>
    /// <param name="provider">The file provider to use for listing.</param>
    /// <param name="volume">The volume (bucket) name.</param>
    /// <param name="path">
    /// The folder path within the volume, using "/" as separator.
    /// Use empty string or null for the volume root.
    /// Example: <c>"documents/reports"</c>
    /// </param>
    public StorageCatalog(IFileProvider provider, string volume, string? path)
    {
        if (string.IsNullOrWhiteSpace(volume))
            throw new ArgumentException("Volume must not be empty.", nameof(volume));

        _provider = provider;
        _volume   = volume;
        _path     = NormalizePath(path);

        var segments = string.IsNullOrEmpty(_path)
            ? Enumerable.Empty<string>()
            : _path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        _folderKey = new StructuredKey(volume, segments);
    }

    /// <summary>The volume this catalog belongs to.</summary>
    public string Volume => _volume;

    /// <summary>The folder path this catalog covers.</summary>
    public string Path => _path;

    // ── Listing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all files in this folder. The listing is cached after the
    /// first call. Use <see cref="Refresh"/> or <see cref="RefreshAsync"/>
    /// to get fresh results.
    /// </summary>
    public async Task<IReadOnlyList<StorageItem>> GetFilesAsync(
        CancellationToken ct = default)
    {
        var all = await EnsureLoadedAsync(ct);
        return all.Where(i => i.IsFile).ToList();
    }

    /// <summary>
    /// Returns all sub-folders in this folder. The listing is cached.
    /// </summary>
    public async Task<IReadOnlyList<StorageItem>> GetFoldersAsync(
        CancellationToken ct = default)
    {
        var all = await EnsureLoadedAsync(ct);
        return all.Where(i => i.IsFolder).ToList();
    }

    /// <summary>
    /// Returns all items (files and folders) in this folder. The listing is cached.
    /// </summary>
    public async Task<IReadOnlyList<StorageItem>> GetAllAsync(
        CancellationToken ct = default)
        => await EnsureLoadedAsync(ct);

    // ── Existence checks ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when a file with the given name exists in this folder.
    /// Case-insensitive.
    /// </summary>
    public async Task<bool> FileExistsAsync(
        string fileName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var all = await EnsureLoadedAsync(ct);
        return all.Any(i => i.IsFile &&
            string.Equals(i.Name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true when a folder with the given name exists in this folder.
    /// Case-insensitive.
    /// </summary>
    public async Task<bool> FolderExistsAsync(
        string folderName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        var all = await EnsureLoadedAsync(ct);
        return all.Any(i => i.IsFolder &&
            string.Equals(i.Name, folderName, StringComparison.OrdinalIgnoreCase));
    }

    // ── Reference extraction ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="FileReference"/> for the named file, pre-populated
    /// with its provider-native identity. Returns null when the file does not exist.
    /// </summary>
    public async Task<FileReference?> GetFileReferenceAsync(
        string fileName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var all = await EnsureLoadedAsync(ct);
        var item = all.FirstOrDefault(i => i.IsFile &&
            string.Equals(i.Name, fileName, StringComparison.OrdinalIgnoreCase));

        return item is null ? null : new FileReference(_volume, _path, item.Name, item);
    }

    /// <summary>
    /// Returns a <see cref="FolderReference"/> for the named sub-folder,
    /// pre-populated with its provider-native identity.
    /// Returns null when the folder does not exist.
    /// </summary>
    public async Task<FolderReference?> GetFolderReferenceAsync(
        string folderName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var all = await EnsureLoadedAsync(ct);
        var item = all.FirstOrDefault(i => i.IsFolder &&
            string.Equals(i.Name, folderName, StringComparison.OrdinalIgnoreCase));

        return item is null ? null : new FolderReference(_volume, _path, item.Name, item);
    }

    // ── Cache management ──────────────────────────────────────────────────────

    /// <summary>
    /// Invalidates the cached listing. The next access will trigger a fresh
    /// API call. Does not make an API call itself.
    /// </summary>
    public void Refresh()
    {
        _lock.Wait();
        try { _cache = null; }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Invalidates the cached listing and immediately fetches a fresh one
    /// from the provider.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { _cache = null; }
        finally { _lock.Release(); }

        await EnsureLoadedAsync(ct);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<StorageItem>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock.
            if (_cache is not null) return _cache;

            var result = await _provider.ListAsync(_folderKey, ct);
            if (result.Failed)
                throw new InvalidOperationException(
                    $"Could not load folder '{_folderKey}': {result.Message}");

            _cache = result.Value!;
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return string.Join('/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0));
    }
}
