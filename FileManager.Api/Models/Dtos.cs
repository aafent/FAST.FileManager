namespace FAST.FileManager.Api.Models;

// ── Shared DTOs ───────────────────────────────────────────────────────────────
// These are the JSON shapes that travel between the API and the WASM client.
// The client provider (FAST.FileManager.Providers.Api) uses the same records.

/// <summary>A volume as returned by the API.</summary>
public record VolumeDto(string Id, string DisplayName);

/// <summary>A file or folder item as returned by the API.</summary>
public record StorageItemDto(
    string Kind,           // "File" or "Folder"
    string VolumeId,
    string[] Segments,
    string Name,
    string Title,
    long Size,
    DateTimeOffset? LastModified,
    string? ContentType,
    string? Owner,
    bool ReadOnly,
    string? Tag,
    string? AppReference,
    string? FileReference   // provider-native reference (opaque to the client)
);

/// <summary>Result returned by all mutating operations.</summary>
public record OperationResultDto(
    bool Success,
    string? Error,         // FileOperationError enum name, null on success
    string? Message,
    string? FileReference, // resulting item reference on success
    StorageItemDto? Item   // resulting item on success (create/rename/move/copy/upload)
);

// ── Request bodies ────────────────────────────────────────────────────────────

public record CreateFolderRequest(
    string VolumeId,
    string[] ParentSegments,
    string Name
);

public record DeleteRequest(StorageItemDto Item);

public record RenameRequest(StorageItemDto Item, string NewName);

public record MoveRequest(
    StorageItemDto Item,
    string TargetVolumeId,
    string[] TargetSegments
);

public record CopyRequest(
    StorageItemDto Item,
    string TargetVolumeId,
    string[] TargetSegments
);

public record DuplicateRequest(StorageItemDto Item, string NewName);
