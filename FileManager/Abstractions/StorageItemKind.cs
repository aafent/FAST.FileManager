namespace FAST.FileManager.Abstractions;

/// <summary>
/// Identifies whether a <see cref="StorageItem"/> represents a file or a folder.
/// </summary>
public enum StorageItemKind
{
    /// <summary>A file (an object with content).</summary>
    File = 0,

    /// <summary>
    /// A folder (a container for other items). For backends without native
    /// folders, such as S3, the provider emulates folder semantics.
    /// </summary>
    Folder = 1,
}
