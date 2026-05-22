namespace FAST.FileManager.Abstractions;

/// <summary>
/// The outcome of a file operation. Providers never throw across the
/// abstraction boundary; they report every outcome through this type.
/// </summary>
/// <remarks>
/// On success, <see cref="ResultReference"/> carries the provider-native
/// <see cref="StorageItem.FileReference"/> of the item the operation produced
/// or affected (the created folder, the uploaded file, the renamed/moved/copied
/// item in its new location). On failure, <see cref="Error"/> describes the
/// category and <see cref="Exception"/> carries the originating exception when
/// one occurred.
/// </remarks>
public class FileOperationResult
{
    /// <summary>
    /// Use the <see cref="Ok"/> and <see cref="Fail"/> factory methods to
    /// create instances.
    /// </summary>
    protected FileOperationResult(
        bool success,
        FileOperationError error,
        string? message,
        Exception? exception,
        object? resultReference)
    {
        Success = success;
        Error = error;
        Message = message;
        Exception = exception;
        ResultReference = resultReference;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool Success { get; }

    /// <summary>True when the operation failed. The inverse of <see cref="Success"/>.</summary>
    public bool Failed => !Success;

    /// <summary>
    /// The failure category. <see cref="FileOperationError.None"/> on success.
    /// </summary>
    public FileOperationError Error { get; }

    /// <summary>
    /// A human-readable message describing the outcome, suitable for display
    /// in the component's error window. May be null.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// The originating exception, when the failure was caused by one.
    /// Null on success and for failures that did not involve an exception.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// The provider-native reference of the item produced or affected by the
    /// operation. Set on success for operations that create or relocate an
    /// item; null otherwise.
    /// </summary>
    public object? ResultReference { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="resultReference">
    /// The provider-native reference of the affected item, if any.
    /// </param>
    public static FileOperationResult Ok(object? resultReference = null) =>
        new(true, FileOperationError.None, null, null, resultReference);

    /// <summary>Creates a failed result.</summary>
    public static FileOperationResult Fail(
        FileOperationError error,
        string? message = null,
        Exception? exception = null) =>
        new(false, error, message, exception, null);
}

/// <summary>
/// The outcome of a file operation that also returns a value on success,
/// such as a directory listing or a download stream.
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
public sealed class FileOperationResult<T> : FileOperationResult
{
    private FileOperationResult(
        bool success,
        FileOperationError error,
        string? message,
        Exception? exception,
        object? resultReference,
        T? value)
        : base(success, error, message, exception, resultReference)
    {
        Value = value;
    }

    /// <summary>
    /// The value produced by the operation on success; default(T) on failure.
    /// </summary>
    public T? Value { get; }

    /// <summary>Creates a successful result carrying a value.</summary>
    /// <param name="value">The value produced by the operation.</param>
    /// <param name="resultReference">
    /// The provider-native reference of the affected item, if any.
    /// </param>
    public static FileOperationResult<T> Ok(T value, object? resultReference = null) =>
        new(true, FileOperationError.None, null, null, resultReference, value);

    /// <summary>Creates a failed result.</summary>
    public static new FileOperationResult<T> Fail(
        FileOperationError error,
        string? message = null,
        Exception? exception = null) =>
        new(false, error, message, exception, null, default);
}
