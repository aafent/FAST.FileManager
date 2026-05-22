using FAST.FileManager.Abstractions;
using System.IO;

namespace FAST.FileManager.SDK;

/// <summary>
/// A high-level developer SDK for file and folder operations over any
/// <see cref="IFileProvider"/> backend (S3, FAST, etc.).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileManagerClient"/> provides a clean, provider-agnostic API
/// similar in spirit to .NET's <c>File</c> and <c>Directory</c> classes.
/// All addressing uses three explicit parts: volume, path, and name.
/// </para>
/// <para>
/// Operations that act on existing items (delete, rename, move, copy,
/// duplicate, download) take a <see cref="FileReference"/> or
/// <see cref="FolderReference"/>. Obtain references from a
/// <see cref="StorageCatalog"/> for maximum efficiency, or construct them
/// directly from the three address parts.
/// </para>
/// <para>
/// Use <see cref="StorageCatalog"/> for listing and existence checks —
/// <see cref="FileManagerClient"/> does not expose listing methods.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var client = new FileManagerClient(provider);
/// var catalog = new StorageCatalog(provider, "my-bucket", "documents");
///
/// // Upload
/// await using var stream = File.OpenRead("report.pdf");
/// await client.UploadFileAsync("my-bucket", "documents", "report.pdf", stream);
///
/// // Download
/// var fileRef = await catalog.GetFileReferenceAsync("report.pdf");
/// if (fileRef is not null)
/// {
///     var download = await client.DownloadFileAsync(fileRef);
///     // use download.Value stream
/// }
/// </code>
/// </example>
public sealed class FileManagerClient
{
    private readonly IFileProvider _provider;

    /// <summary>Creates a new <see cref="FileManagerClient"/>.</summary>
    /// <param name="provider">The file provider to use for all operations.</param>
    public FileManagerClient(IFileProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FOLDER OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new folder inside the specified parent location.
    /// </summary>
    /// <param name="volume">The volume (bucket) name.</param>
    /// <param name="path">
    /// The path of the parent folder. Empty or null for the volume root.
    /// </param>
    /// <param name="folderName">The name of the new folder.</param>
    public async Task<FileOperationResult<FolderReference>> CreateFolderAsync(
        string volume,
        string? path,
        string folderName,
        CancellationToken ct = default)
    {
        var parentKey = BuildKey(volume, path);
        var result = await _provider.CreateFolderAsync(parentKey, folderName, ct);

        if (result.Failed)
            return FileOperationResult<FolderReference>.Fail(
                result.Error, result.Message, result.Exception);

        var folderRef = new FolderReference(volume, path, folderName, result.Value!);
        return FileOperationResult<FolderReference>.Ok(folderRef, result.ResultReference);
    }

    /// <summary>
    /// Deletes the folder identified by <paramref name="folder"/> and all
    /// its contents.
    /// </summary>
    public async Task<FileOperationResult> DeleteFolderAsync(
        FolderReference folder,
        CancellationToken ct = default)
    {
        var item = await ResolveFolder(folder, ct);
        if (item is null)
            return FileOperationResult.Fail(FileOperationError.NotFound,
                $"Folder '{folder}' not found.");

        return await _provider.DeleteAsync(item, ct);
    }

    /// <summary>
    /// Renames the folder identified by <paramref name="folder"/>.
    /// </summary>
    public async Task<FileOperationResult<FolderReference>> RenameFolderAsync(
        FolderReference folder,
        string newName,
        CancellationToken ct = default)
    {
        var item = await ResolveFolder(folder, ct);
        if (item is null)
            return FileOperationResult<FolderReference>.Fail(FileOperationError.NotFound,
                $"Folder '{folder}' not found.");

        var result = await _provider.RenameAsync(item, newName, ct);
        if (result.Failed)
            return FileOperationResult<FolderReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FolderReference(folder.Volume, folder.Path, newName, result.Value!);
        return FileOperationResult<FolderReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Moves the folder identified by <paramref name="folder"/> into the
    /// specified target location.
    /// </summary>
    public async Task<FileOperationResult<FolderReference>> MoveFolderAsync(
        FolderReference folder,
        string targetVolume,
        string? targetPath,
        CancellationToken ct = default)
    {
        var item = await ResolveFolder(folder, ct);
        if (item is null)
            return FileOperationResult<FolderReference>.Fail(FileOperationError.NotFound,
                $"Folder '{folder}' not found.");

        var targetKey = BuildKey(targetVolume, targetPath);
        var result = await _provider.MoveAsync(item, targetKey, ct);
        if (result.Failed)
            return FileOperationResult<FolderReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FolderReference(targetVolume, targetPath, folder.FolderName, result.Value!);
        return FileOperationResult<FolderReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Copies the folder identified by <paramref name="folder"/> into the
    /// specified target location.
    /// </summary>
    public async Task<FileOperationResult<FolderReference>> CopyFolderAsync(
        FolderReference folder,
        string targetVolume,
        string? targetPath,
        CancellationToken ct = default)
    {
        var item = await ResolveFolder(folder, ct);
        if (item is null)
            return FileOperationResult<FolderReference>.Fail(FileOperationError.NotFound,
                $"Folder '{folder}' not found.");

        var targetKey = BuildKey(targetVolume, targetPath);
        var result = await _provider.CopyAsync(item, targetKey, ct);
        if (result.Failed)
            return FileOperationResult<FolderReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FolderReference(targetVolume, targetPath, folder.FolderName, result.Value!);
        return FileOperationResult<FolderReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Duplicates the folder identified by <paramref name="folder"/> within
    /// its current location. The duplicate is named automatically
    /// (e.g. "reports (2)").
    /// </summary>
    public async Task<FileOperationResult<FolderReference>> DuplicateFolderAsync(
        FolderReference folder,
        CancellationToken ct = default)
    {
        var item = await ResolveFolder(folder, ct);
        if (item is null)
            return FileOperationResult<FolderReference>.Fail(FileOperationError.NotFound,
                $"Folder '{folder}' not found.");

        var newName = await GenerateDuplicateNameAsync(
            folder.Volume, folder.Path, folder.FolderName, isFolder: true, ct);

        var result = await _provider.DuplicateAsync(item, newName, ct);
        if (result.Failed)
            return FileOperationResult<FolderReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FolderReference(folder.Volume, folder.Path, newName, result.Value!);
        return FileOperationResult<FolderReference>.Ok(newRef, result.ResultReference);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FILE OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deletes the file identified by <paramref name="file"/>.
    /// </summary>
    public async Task<FileOperationResult> DeleteFileAsync(
        FileReference file,
        CancellationToken ct = default)
    {
        var item = await ResolveFile(file, ct);
        if (item is null)
            return FileOperationResult.Fail(FileOperationError.NotFound,
                $"File '{file}' not found.");

        return await _provider.DeleteAsync(item, ct);
    }

    /// <summary>
    /// Renames the file identified by <paramref name="file"/>.
    /// </summary>
    public async Task<FileOperationResult<FileReference>> RenameFileAsync(
        FileReference file,
        string newName,
        CancellationToken ct = default)
    {
        var item = await ResolveFile(file, ct);
        if (item is null)
            return FileOperationResult<FileReference>.Fail(FileOperationError.NotFound,
                $"File '{file}' not found.");

        var result = await _provider.RenameAsync(item, newName, ct);
        if (result.Failed)
            return FileOperationResult<FileReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FileReference(file.Volume, file.Path, newName, result.Value!);
        return FileOperationResult<FileReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Moves the file identified by <paramref name="file"/> into the
    /// specified target location.
    /// </summary>
    public async Task<FileOperationResult<FileReference>> MoveFileAsync(
        FileReference file,
        string targetVolume,
        string? targetPath,
        CancellationToken ct = default)
    {
        var item = await ResolveFile(file, ct);
        if (item is null)
            return FileOperationResult<FileReference>.Fail(FileOperationError.NotFound,
                $"File '{file}' not found.");

        var targetKey = BuildKey(targetVolume, targetPath);
        var result = await _provider.MoveAsync(item, targetKey, ct);
        if (result.Failed)
            return FileOperationResult<FileReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FileReference(targetVolume, targetPath, file.FileName, result.Value!);
        return FileOperationResult<FileReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Copies the file identified by <paramref name="file"/> into the
    /// specified target location.
    /// </summary>
    public async Task<FileOperationResult<FileReference>> CopyFileAsync(
        FileReference file,
        string targetVolume,
        string? targetPath,
        CancellationToken ct = default)
    {
        var item = await ResolveFile(file, ct);
        if (item is null)
            return FileOperationResult<FileReference>.Fail(FileOperationError.NotFound,
                $"File '{file}' not found.");

        var targetKey = BuildKey(targetVolume, targetPath);
        var result = await _provider.CopyAsync(item, targetKey, ct);
        if (result.Failed)
            return FileOperationResult<FileReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FileReference(targetVolume, targetPath, file.FileName, result.Value!);
        return FileOperationResult<FileReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Duplicates the file identified by <paramref name="file"/> within its
    /// current folder. The duplicate is named automatically
    /// (e.g. "report (2).pdf").
    /// </summary>
    public async Task<FileOperationResult<FileReference>> DuplicateFileAsync(
        FileReference file,
        CancellationToken ct = default)
    {
        var item = await ResolveFile(file, ct);
        if (item is null)
            return FileOperationResult<FileReference>.Fail(FileOperationError.NotFound,
                $"File '{file}' not found.");

        var newName = await GenerateDuplicateNameAsync(
            file.Volume, file.Path, file.FileName, isFolder: false, ct);

        var result = await _provider.DuplicateAsync(item, newName, ct);
        if (result.Failed)
            return FileOperationResult<FileReference>.Fail(
                result.Error, result.Message, result.Exception);

        var newRef = new FileReference(file.Volume, file.Path, newName, result.Value!);
        return FileOperationResult<FileReference>.Ok(newRef, result.ResultReference);
    }

    /// <summary>
    /// Uploads a file to the specified location.
    /// </summary>
    /// <param name="volume">The target volume (bucket) name.</param>
    /// <param name="path">The target folder path. Empty or null for the volume root.</param>
    /// <param name="fileName">The file name including extension.</param>
    /// <param name="content">The file content stream. The caller owns and disposes the stream.</param>
    /// <param name="contentType">Optional MIME type. Guessed from the extension when null.</param>
    public async Task<FileOperationResult<FileReference>> UploadFileAsync(
        string volume,
        string? path,
        string fileName,
        Stream content,
        string? contentType = null,
        CancellationToken ct = default)
    {
        var targetKey = BuildKey(volume, path);
        var result = await _provider.UploadAsync(targetKey, fileName, content, contentType, ct);

        if (result.Failed)
            return FileOperationResult<FileReference>.Fail(
                result.Error, result.Message, result.Exception);

        var fileRef = new FileReference(volume, path, fileName, result.Value!);
        return FileOperationResult<FileReference>.Ok(fileRef, result.ResultReference);
    }

    /// <summary>
    /// Downloads the file identified by <paramref name="file"/>.
    /// </summary>
    /// <returns>
    /// On success, a readable <see cref="Stream"/> of the file content.
    /// The caller owns and must dispose the stream.
    /// </returns>
    public async Task<FileOperationResult<Stream>> DownloadFileAsync(
        FileReference file,
        CancellationToken ct = default)
    {
        var item = await ResolveFile(file, ct);
        if (item is null)
            return FileOperationResult<Stream>.Fail(FileOperationError.NotFound,
                $"File '{file}' not found.");

        return await _provider.DownloadAsync(item, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a <see cref="FileReference"/> to a <see cref="StorageItem"/>.
    /// Uses the pre-populated item when available (from a catalog lookup),
    /// otherwise lists the parent folder to find it.
    /// </summary>
    private async Task<StorageItem?> ResolveFile(FileReference file, CancellationToken ct)
    {
        // Fast path: reference was obtained from a StorageCatalog.
        if (file.Item is not null) return file.Item;

        // Slow path: list the parent folder and find the item by name.
        var parentKey = file.ToParentKey();
        var result = await _provider.ListAsync(parentKey, ct);
        if (result.Failed) return null;

        return result.Value!.FirstOrDefault(i =>
            i.IsFile &&
            string.Equals(i.Name, file.FileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a <see cref="FolderReference"/> to a <see cref="StorageItem"/>.
    /// Uses the pre-populated item when available (from a catalog lookup),
    /// otherwise lists the parent folder to find it.
    /// </summary>
    private async Task<StorageItem?> ResolveFolder(FolderReference folder, CancellationToken ct)
    {
        // Fast path: reference was obtained from a StorageCatalog.
        if (folder.Item is not null) return folder.Item;

        // Slow path: list the parent folder and find the item by name.
        var parentKey = folder.ToParentKey();
        var result = await _provider.ListAsync(parentKey, ct);
        if (result.Failed) return null;

        return result.Value!.FirstOrDefault(i =>
            i.IsFolder &&
            string.Equals(i.Name, folder.FolderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a <see cref="StructuredKey"/> from volume and path string.
    /// </summary>
    private static StructuredKey BuildKey(string volume, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new StructuredKey(volume);

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new StructuredKey(volume, segments);
    }

    /// <summary>
    /// Generates a non-conflicting duplicate name by listing the parent folder.
    /// Files:   "report.pdf"  → "report (2).pdf"
    /// Folders: "docs"        → "docs (2)"
    /// </summary>
    private async Task<string> GenerateDuplicateNameAsync(
        string volume,
        string? path,
        string name,
        bool isFolder,
        CancellationToken ct)
    {
        var parentKey = BuildKey(volume, path);
        var listResult = await _provider.ListAsync(parentKey, ct);
        var existing = listResult.Success
            ? listResult.Value!.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ext   = isFolder ? string.Empty : System.IO.Path.GetExtension(name);
        var title = isFolder ? name : System.IO.Path.GetFileNameWithoutExtension(name);

        for (var n = 2; n < 10000; n++)
        {
            var candidate = isFolder ? $"{title} ({n})" : $"{title} ({n}){ext}";
            if (!existing.Contains(candidate))
                return candidate;
        }

        return isFolder ? $"{title} (copy)" : $"{title} (copy){ext}";
    }
}
