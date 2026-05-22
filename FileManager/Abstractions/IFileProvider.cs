using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FAST.FileManager.Abstractions;

/// <summary>
/// A transport-agnostic file backend. The file manager component depends only
/// on this interface; concrete providers (S3, and later others) implement it.
/// </summary>
/// <remarks>
/// <para>
/// Implementations never throw across this boundary for expected failures:
/// every method reports its outcome through a <see cref="FileOperationResult"/>.
/// Implementations should also translate cancellation and unexpected
/// exceptions into a result rather than letting them propagate.
/// </para>
/// <para>
/// One provider instance backs one component instance. Folder semantics are
/// first-class here; providers whose backend lacks native folders emulate
/// them internally.
/// </para>
/// </remarks>
public interface IFileProvider
{
    /// <summary>
    /// Returns the set of operations this provider supports, so the component
    /// can hide or disable unsupported actions.
    /// </summary>
    Capabilities GetCapabilities();

    /// <summary>
    /// Lists the volumes available from this provider. These appear in the
    /// component's left-hand panel.
    /// </summary>
    Task<FileOperationResult<IReadOnlyList<Volume>>> GetVolumesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the immediate contents — files and folders — of the folder
    /// addressed by <paramref name="folder"/>. The whole folder is returned in
    /// one result; the provider performs any backend pagination internally.
    /// The result contains no synthetic "." or ".." entries.
    /// </summary>
    /// <param name="folder">
    /// The folder to list. Use a volume's root key to list the volume root.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<FileOperationResult<IReadOnlyList<StorageItem>>> ListAsync(
        StructuredKey folder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new folder named <paramref name="name"/> inside the folder
    /// addressed by <paramref name="parent"/>. Fails with
    /// <see cref="FileOperationError.Conflict"/> if an item of that name
    /// already exists.
    /// </summary>
    /// <returns>
    /// On success, the newly created folder as a fully populated
    /// <see cref="StorageItem"/>.
    /// </returns>
    Task<FileOperationResult<StorageItem>> CreateFolderAsync(
        StructuredKey parent,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the given file or folder. Deleting a folder removes everything
    /// it contains.
    /// </summary>
    /// <param name="item">The item to delete.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<FileOperationResult> DeleteAsync(
        StorageItem item,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the given file or folder to <paramref name="newName"/> within
    /// its current parent folder. Fails with
    /// <see cref="FileOperationError.Conflict"/> if an item of that name
    /// already exists.
    /// </summary>
    /// <returns>
    /// On success, the renamed item as a fully populated
    /// <see cref="StorageItem"/> in its new identity.
    /// </returns>
    Task<FileOperationResult<StorageItem>> RenameAsync(
        StorageItem item,
        string newName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the given file or folder into the folder addressed by
    /// <paramref name="targetFolder"/>. Fails with
    /// <see cref="FileOperationError.Conflict"/> if an item of the same name
    /// already exists in the target.
    /// </summary>
    /// <returns>
    /// On success, the moved item as a fully populated
    /// <see cref="StorageItem"/> in its new location.
    /// </returns>
    Task<FileOperationResult<StorageItem>> MoveAsync(
        StorageItem item,
        StructuredKey targetFolder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the given file or folder within its current folder under a new
    /// name. Unlike <see cref="CopyAsync"/>, the destination is always the
    /// same folder as the source; only the name changes.
    /// </summary>
    /// <returns>
    /// On success, the new copy as a fully populated <see cref="StorageItem"/>.
    /// </returns>
    Task<FileOperationResult<StorageItem>> DuplicateAsync(
        StorageItem item,
        string newName,
        CancellationToken cancellationToken = default);
    /// <returns>
    /// On success, the new copy as a fully populated
    /// <see cref="StorageItem"/>.
    /// </returns>
    Task<FileOperationResult<StorageItem>> CopyAsync(
        StorageItem item,
        StructuredKey targetFolder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file named <paramref name="name"/> into the folder addressed
    /// by <paramref name="targetFolder"/>, reading its content from
    /// <paramref name="content"/>. Fails with
    /// <see cref="FileOperationError.Conflict"/> if an item of that name
    /// already exists.
    /// </summary>
    /// <param name="targetFolder">The folder to upload into.</param>
    /// <param name="name">The name for the uploaded file, including extension.</param>
    /// <param name="content">
    /// The file content. The caller owns and disposes this stream.
    /// </param>
    /// <param name="contentType">
    /// The MIME content type to associate with the file, if known.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// On success, the newly created file as a fully populated
    /// <see cref="StorageItem"/>.
    /// </returns>
    Task<FileOperationResult<StorageItem>> UploadAsync(
        StructuredKey targetFolder,
        string name,
        Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the content of the given file for reading.
    /// </summary>
    /// <param name="item">The file to download.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// On success, a readable stream of the file content. The caller owns and
    /// disposes the stream.
    /// </returns>
    Task<FileOperationResult<Stream>> DownloadAsync(
        StorageItem item,
        CancellationToken cancellationToken = default);
}
