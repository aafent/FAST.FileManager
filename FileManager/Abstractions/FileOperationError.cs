namespace FAST.FileManager.Abstractions;

/// <summary>
/// The category of a failed file operation. Providers never throw across the
/// abstraction boundary; instead they report failure through a
/// <see cref="FileOperationResult"/> carrying one of these values.
/// </summary>
public enum FileOperationError
{
    /// <summary>No error. The operation succeeded.</summary>
    None = 0,

    /// <summary>The target item or folder does not exist.</summary>
    NotFound = 1,

    /// <summary>
    /// An item with the requested name already exists. Per design, the
    /// component rejects the operation rather than overwriting.
    /// </summary>
    Conflict = 2,

    /// <summary>
    /// The provider, the volume, or the specific item does not permit this
    /// operation (for example a read-only item or an unsupported capability).
    /// </summary>
    NotPermitted = 3,

    /// <summary>The supplied name, key, or argument was not valid.</summary>
    InvalidArgument = 4,

    /// <summary>
    /// Authentication or authorization with the backend failed
    /// (for example an S3 SignatureDoesNotMatch or AccessDenied).
    /// </summary>
    AuthenticationFailed = 5,

    /// <summary>
    /// A network or transport-level failure occurred while contacting the
    /// backend.
    /// </summary>
    NetworkError = 6,

    /// <summary>The operation was cancelled via a cancellation token.</summary>
    Cancelled = 7,

    /// <summary>
    /// An unexpected error occurred. Inspect
    /// <see cref="FileOperationResult.Exception"/> for details.
    /// </summary>
    Unknown = 100,
}
