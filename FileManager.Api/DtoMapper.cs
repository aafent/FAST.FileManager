using FAST.FileManager.Abstractions;
using FAST.FileManager.Api.Models;

namespace FAST.FileManager.Api;

/// <summary>
/// Converts between <see cref="FileManager.Abstractions"/> types and the
/// API DTOs that travel over the wire.
/// </summary>
internal static class DtoMapper
{
    public static VolumeDto ToDto(Volume v) =>
        new(v.Id, v.DisplayName);

    public static StorageItemDto ToDto(StorageItem item) =>
        new(
            Kind:          item.Kind.ToString(),
            VolumeId:      item.Key.VolumeId,
            Segments:      item.Key.Segments.ToArray(),
            Name:          item.Name,
            Title:         item.Title,
            Size:          item.Size,
            LastModified:  item.LastModified,
            ContentType:   item.ContentType,
            Owner:         item.Owner,
            ReadOnly:      item.ReadOnly,
            Tag:           item.Tag?.ToString(),
            AppReference:  item.AppReference?.ToString(),
            FileReference: item.FileReference?.ToString()
        );

    public static StorageItem FromDto(StorageItemDto dto)
    {
        var kind = Enum.Parse<StorageItemKind>(dto.Kind);
        var key  = new StructuredKey(dto.VolumeId, dto.Segments);
        return new StorageItem(kind, key, dto.Name)
        {
            Size          = dto.Size,
            LastModified  = dto.LastModified,
            ContentType   = dto.ContentType,
            Owner         = dto.Owner,
            ReadOnly      = dto.ReadOnly,
            Tag           = dto.Tag,
            AppReference  = dto.AppReference,
            FileReference = dto.FileReference,
        };
    }

    public static StructuredKey KeyFromDto(string volumeId, string[] segments) =>
        new(volumeId, segments);

    public static OperationResultDto OkDto(StorageItem item) =>
        new(true, null, null, item.FileReference?.ToString(), ToDto(item));

    public static OperationResultDto OkDto() =>
        new(true, null, null, null, null);

    public static OperationResultDto FailDto(FileOperationResult result) =>
        new(false, result.Error.ToString(), result.Message, null, null);
}
