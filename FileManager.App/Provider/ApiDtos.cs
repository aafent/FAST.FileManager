namespace FAST.FileManager.App.Provider;

// ── Wire DTOs ─────────────────────────────────────────────────────────────────
// These mirror FileManager.Api.Models.Dtos exactly.
// Kept separate so the client provider has no dependency on the server project.

internal record VolumeDto(string Id, string DisplayName);

internal record StorageItemDto(
    string Kind,
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
    string? FileReference
);

internal record OperationResultDto(
    bool Success,
    string? Error,
    string? Message,
    string? FileReference,
    StorageItemDto? Item
);

internal record CreateFolderRequest(
    string VolumeId,
    string[] ParentSegments,
    string Name
);

internal record DeleteRequest(StorageItemDto Item);
internal record RenameRequest(StorageItemDto Item, string NewName);

internal record MoveRequest(
    StorageItemDto Item,
    string TargetVolumeId,
    string[] TargetSegments
);

internal record CopyRequest(
    StorageItemDto Item,
    string TargetVolumeId,
    string[] TargetSegments
);

internal record DuplicateRequest(StorageItemDto Item, string NewName);
