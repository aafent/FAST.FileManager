using FAST.FileManager.Abstractions;

namespace FAST.FileManager.Providers.LocalFileSystem;

/// <summary>
/// An <see cref="IFileProvider"/> implementation backed by the server's
/// local filesystem. The configured <see cref="LocalFileSystemOptions.RootPath"/>
/// is exposed as a single volume. All operations are restricted to that
/// root — path traversal outside it is rejected.
/// </summary>
public sealed class LocalFileSystemProvider : IFileProvider
{
    private readonly LocalFileSystemOptions _options;
    private readonly string _root;   // normalised absolute root path
    private readonly string _volumeId;

    public LocalFileSystemProvider(LocalFileSystemOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException(
                "LocalFileSystem RootPath must not be empty.", nameof(options));

        _options  = options;
        _root     = Path.GetFullPath(options.RootPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
        _volumeId = _options.EffectiveVolumeName;

        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException(
                $"LocalFileSystem RootPath does not exist: '{_root}'");
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public Capabilities GetCapabilities() => _options.ReadOnly
        ? new Capabilities { CanDownload = true }
        : new Capabilities
        {
            CanCreateFolder = true,
            CanDelete       = true,
            CanRename       = true,
            CanMove         = true,
            CanCopy         = true,
            CanUpload       = true,
            CanDownload     = true,
            MaxUploadSizeBytes = _options.MaxUploadBytes,
        };

    // ── Volumes ───────────────────────────────────────────────────────────────

    public Task<FileOperationResult<IReadOnlyList<Volume>>> GetVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        var volume = new Volume(_volumeId, _options.EffectiveVolumeName);
        IReadOnlyList<Volume> list = new[] { volume };
        return Task.FromResult(FileOperationResult<IReadOnlyList<Volume>>.Ok(list));
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public Task<FileOperationResult<IReadOnlyList<StorageItem>>> ListAsync(
        StructuredKey folder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dirPath = ResolveDirectory(folder);
            if (!Directory.Exists(dirPath))
                return Task.FromResult(
                    FileOperationResult<IReadOnlyList<StorageItem>>.Fail(
                        FileOperationError.NotFound,
                        $"Directory not found: '{dirPath}'"));

            var items = new List<StorageItem>();

            // Folders first
            foreach (var dir in Directory.EnumerateDirectories(dirPath)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(dir);
                if (IsHidden(info)) continue;
                if (IsSymlink(info)) continue;

                var name = info.Name;
                var key  = folder.GetChild(name);
                items.Add(new StorageItem(StorageItemKind.Folder, key, name)
                {
                    LastModified  = info.LastWriteTimeUtc,
                    FileReference = dir,
                    ReadOnly      = IsReadOnly(info),
                });
            }

            // Files
            foreach (var file in Directory.EnumerateFiles(dirPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                if (IsHidden(info)) continue;
                if (IsSymlink(info)) continue;

                var name = info.Name;
                var key  = folder.GetChild(name);
                items.Add(new StorageItem(StorageItemKind.File, key, name)
                {
                    Size          = info.Length,
                    LastModified  = info.LastWriteTimeUtc,
                    ContentType   = MimeTypeHelper.Guess(name),
                    FileReference = file,
                    ReadOnly      = info.IsReadOnly,
                });
            }

            return Task.FromResult(
                FileOperationResult<IReadOnlyList<StorageItem>>.Ok(items));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                FileOperationResult<IReadOnlyList<StorageItem>>.Fail(
                    FileOperationError.Unknown, ex.Message, ex));
        }
    }

    // ── CreateFolder ──────────────────────────────────────────────────────────

    public Task<FileOperationResult<StorageItem>> CreateFolderAsync(
        StructuredKey parent, string name,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return ReadOnlyResult<StorageItem>();
        try
        {
            ValidateName(name);
            var parentPath = ResolveDirectory(parent);
            var newPath    = Path.Combine(parentPath, name);
            AssertWithinRoot(newPath);

            if (Directory.Exists(newPath) || File.Exists(newPath))
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"An item named '{name}' already exists."));

            Directory.CreateDirectory(newPath);
            var key  = parent.GetChild(name);
            var item = new StorageItem(StorageItemKind.Folder, key, name)
            {
                LastModified  = Directory.GetLastWriteTimeUtc(newPath),
                FileReference = newPath,
            };
            return Task.FromResult(FileOperationResult<StorageItem>.Ok(item, newPath));
        }
        catch (Exception ex) { return Task.FromResult(UnknownFail<StorageItem>(ex)); }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public Task<FileOperationResult> DeleteAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return ReadOnlyResult();
        try
        {
            var path = ResolveItem(item);
            if (item.IsFolder)
            {
                if (!Directory.Exists(path))
                    return Task.FromResult(
                        FileOperationResult.Fail(FileOperationError.NotFound,
                            $"Folder not found: '{path}'"));
                Directory.Delete(path, recursive: true);
            }
            else
            {
                if (!File.Exists(path))
                    return Task.FromResult(
                        FileOperationResult.Fail(FileOperationError.NotFound,
                            $"File not found: '{path}'"));
                File.Delete(path);
            }
            return Task.FromResult(FileOperationResult.Ok());
        }
        catch (Exception ex) { return Task.FromResult(UnknownFail(ex)); }
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    public Task<FileOperationResult<StorageItem>> RenameAsync(
        StorageItem item, string newName,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return ReadOnlyResult<StorageItem>();
        try
        {
            ValidateName(newName);
            var oldPath    = ResolveItem(item);
            var parentPath = Path.GetDirectoryName(oldPath)!;
            var newPath    = Path.Combine(parentPath, newName);
            AssertWithinRoot(newPath);

            if (Directory.Exists(newPath) || File.Exists(newPath))
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"An item named '{newName}' already exists."));

            var parentKey = item.Key.GetParent();
            var newKey    = parentKey.GetChild(newName);

            if (item.IsFolder)
            {
                Directory.Move(oldPath, newPath);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, newName)
                {
                    LastModified  = Directory.GetLastWriteTimeUtc(newPath),
                    FileReference = newPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, newPath));
            }
            else
            {
                File.Move(oldPath, newPath);
                var newItem = new StorageItem(StorageItemKind.File, newKey, newName)
                {
                    Size          = new FileInfo(newPath).Length,
                    LastModified  = File.GetLastWriteTimeUtc(newPath),
                    ContentType   = MimeTypeHelper.Guess(newName),
                    FileReference = newPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, newPath));
            }
        }
        catch (Exception ex) { return Task.FromResult(UnknownFail<StorageItem>(ex)); }
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    public Task<FileOperationResult<StorageItem>> MoveAsync(
        StorageItem item, StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return ReadOnlyResult<StorageItem>();
        try
        {
            var sourcePath = ResolveItem(item);
            var targetDir  = ResolveDirectory(targetFolder);
            var destPath   = Path.Combine(targetDir, item.Name);
            AssertWithinRoot(destPath);

            if (Directory.Exists(destPath) || File.Exists(destPath))
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"An item named '{item.Name}' already exists in the target."));

            var newKey = targetFolder.GetChild(item.Name);

            if (item.IsFolder)
            {
                Directory.Move(sourcePath, destPath);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, item.Name)
                {
                    LastModified  = Directory.GetLastWriteTimeUtc(destPath),
                    FileReference = destPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, destPath));
            }
            else
            {
                File.Move(sourcePath, destPath);
                var newItem = new StorageItem(StorageItemKind.File, newKey, item.Name)
                {
                    Size          = new FileInfo(destPath).Length,
                    LastModified  = File.GetLastWriteTimeUtc(destPath),
                    ContentType   = item.ContentType,
                    FileReference = destPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, destPath));
            }
        }
        catch (Exception ex) { return Task.FromResult(UnknownFail<StorageItem>(ex)); }
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    public Task<FileOperationResult<StorageItem>> CopyAsync(
        StorageItem item, StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return ReadOnlyResult<StorageItem>();
        try
        {
            var sourcePath = ResolveItem(item);
            var targetDir  = ResolveDirectory(targetFolder);
            var destPath   = Path.Combine(targetDir, item.Name);
            AssertWithinRoot(destPath);

            if (Directory.Exists(destPath) || File.Exists(destPath))
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Fail(
                        FileOperationError.Conflict,
                        $"An item named '{item.Name}' already exists in the target."));

            var newKey = targetFolder.GetChild(item.Name);

            if (item.IsFolder)
            {
                CopyDirectory(sourcePath, destPath);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, item.Name)
                {
                    LastModified  = Directory.GetLastWriteTimeUtc(destPath),
                    FileReference = destPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, destPath));
            }
            else
            {
                File.Copy(sourcePath, destPath);
                var newItem = new StorageItem(StorageItemKind.File, newKey, item.Name)
                {
                    Size          = new FileInfo(destPath).Length,
                    LastModified  = File.GetLastWriteTimeUtc(destPath),
                    ContentType   = item.ContentType,
                    FileReference = destPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, destPath));
            }
        }
        catch (Exception ex) { return Task.FromResult(UnknownFail<StorageItem>(ex)); }
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    public Task<FileOperationResult<StorageItem>> DuplicateAsync(
        StorageItem item, string newName,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return ReadOnlyResult<StorageItem>();
        try
        {
            var sourcePath = ResolveItem(item);
            var parentPath = Path.GetDirectoryName(sourcePath)!;
            var destPath   = Path.Combine(parentPath, newName);
            AssertWithinRoot(destPath);

            var parentKey = item.Key.GetParent();
            var newKey    = parentKey.GetChild(newName);

            if (item.IsFolder)
            {
                CopyDirectory(sourcePath, destPath);
                var newItem = new StorageItem(StorageItemKind.Folder, newKey, newName)
                {
                    LastModified  = Directory.GetLastWriteTimeUtc(destPath),
                    FileReference = destPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, destPath));
            }
            else
            {
                File.Copy(sourcePath, destPath);
                var newItem = new StorageItem(StorageItemKind.File, newKey, newName)
                {
                    Size          = new FileInfo(destPath).Length,
                    LastModified  = File.GetLastWriteTimeUtc(destPath),
                    ContentType   = MimeTypeHelper.Guess(newName),
                    FileReference = destPath,
                };
                return Task.FromResult(
                    FileOperationResult<StorageItem>.Ok(newItem, destPath));
            }
        }
        catch (Exception ex) { return Task.FromResult(UnknownFail<StorageItem>(ex)); }
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> UploadAsync(
        StructuredKey targetFolder, string name, Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (_options.ReadOnly) return await ReadOnlyResult<StorageItem>();
        try
        {
            ValidateName(name);
            var dirPath  = ResolveDirectory(targetFolder);
            var destPath = Path.Combine(dirPath, name);
            AssertWithinRoot(destPath);

            if (File.Exists(destPath))
                return FileOperationResult<StorageItem>.Fail(
                    FileOperationError.Conflict,
                    $"A file named '{name}' already exists.");

            // Enforce upload size limit if configured.
            if (_options.MaxUploadBytes.HasValue
                && content.CanSeek
                && content.Length > _options.MaxUploadBytes.Value)
            {
                return FileOperationResult<StorageItem>.Fail(
                    FileOperationError.InvalidArgument,
                    $"File exceeds maximum upload size of " +
                    $"{_options.MaxUploadBytes.Value / (1024 * 1024)} MB.");
            }

            await using var fs = new FileStream(
                destPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true);

            // Enforce limit for non-seekable streams by counting bytes written.
            if (_options.MaxUploadBytes.HasValue)
            {
                var limit   = _options.MaxUploadBytes.Value;
                var written = 0L;
                var buffer  = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > limit)
                    {
                        fs.Close();
                        File.Delete(destPath);
                        return FileOperationResult<StorageItem>.Fail(
                            FileOperationError.InvalidArgument,
                            $"File exceeds maximum upload size of " +
                            $"{limit / (1024 * 1024)} MB.");
                    }
                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            else
            {
                await content.CopyToAsync(fs, cancellationToken);
            }

            var key  = targetFolder.GetChild(name);
            var info = new FileInfo(destPath);
            var item = new StorageItem(StorageItemKind.File, key, name)
            {
                Size          = info.Length,
                LastModified  = info.LastWriteTimeUtc,
                ContentType   = contentType ?? MimeTypeHelper.Guess(name),
                FileReference = destPath,
            };
            return FileOperationResult<StorageItem>.Ok(item, destPath);
        }
        catch (Exception ex) { return UnknownFail<StorageItem>(ex); }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public Task<FileOperationResult<Stream>> DownloadAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = ResolveItem(item);
            if (!File.Exists(path))
                return Task.FromResult(
                    FileOperationResult<Stream>.Fail(
                        FileOperationError.NotFound,
                        $"File not found: '{path}'"));

            Stream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);

            return Task.FromResult(
                FileOperationResult<Stream>.Ok(stream, path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(UnknownFail<Stream>(ex));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a <see cref="StructuredKey"/> to an absolute directory path,
    /// verifying it stays within the root.
    /// </summary>
    private string ResolveDirectory(StructuredKey key)
    {
        var path = key.IsRoot
            ? _root
            : Path.GetFullPath(Path.Combine(
                new[] { _root }.Concat(key.Segments).ToArray()));
        AssertWithinRoot(path);
        return path;
    }

    /// <summary>
    /// Resolves a <see cref="StorageItem"/> to its absolute path using
    /// the provider-native <see cref="StorageItem.FileReference"/> when
    /// available, falling back to key-based resolution.
    /// </summary>
    private string ResolveItem(StorageItem item)
    {
        if (item.FileReference is string p && !string.IsNullOrEmpty(p))
        {
            AssertWithinRoot(p);
            return p;
        }

        // Fall back to key-based resolution.
        var path = Path.GetFullPath(Path.Combine(
            new[] { _root }.Concat(item.Key.Segments).ToArray()));
        AssertWithinRoot(path);
        return path;
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> if <paramref name="path"/>
    /// is not within <see cref="_root"/>. Prevents path traversal attacks.
    /// </summary>
    private void AssertWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, _root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: path '{fullPath}' is outside the root '{_root}'.");
        }
    }

    /// <summary>Validates a file or folder name — no path separators or nulls.</summary>
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.");

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException(
                $"Name '{name}' contains invalid characters.");

        if (name == "." || name == "..")
            throw new ArgumentException("Name must not be '.' or '..'.");
    }

    /// <summary>Returns true for hidden files/folders (Windows Hidden attribute or dot-prefix).</summary>
    private static bool IsHidden(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.')) return true;
        if ((info.Attributes & FileAttributes.Hidden) != 0) return true;
        return false;
    }

    /// <summary>Returns true for symbolic links — ignored per design.</summary>
    private static bool IsSymlink(FileSystemInfo info)
        => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Returns true if a directory is read-only (no write permission).</summary>
    private static bool IsReadOnly(DirectoryInfo info)
        => (info.Attributes & FileAttributes.ReadOnly) != 0;

    /// <summary>Recursively copies a directory.</summary>
    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, destFile);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var info = new DirectoryInfo(dir);
            if (IsHidden(info) || IsSymlink(info)) continue;
            CopyDirectory(dir, Path.Combine(dest, info.Name));
        }
    }

    // ── Result helpers ────────────────────────────────────────────────────────

    private static Task<FileOperationResult<T>> ReadOnlyResult<T>() =>
        Task.FromResult(FileOperationResult<T>.Fail(
            FileOperationError.NotPermitted,
            "This file system is configured as read-only."));

    private static Task<FileOperationResult> ReadOnlyResult() =>
        Task.FromResult(FileOperationResult.Fail(
            FileOperationError.NotPermitted,
            "This file system is configured as read-only."));

    private static FileOperationResult<T> UnknownFail<T>(Exception ex) =>
        FileOperationResult<T>.Fail(FileOperationError.Unknown, ex.Message, ex);

    private static FileOperationResult UnknownFail(Exception ex) =>
        FileOperationResult.Fail(FileOperationError.Unknown, ex.Message, ex);
}
