using FAST.FileManager.Abstractions;
using FAST.FileManager.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FAST.FileManager.Api.Endpoints;

/// <summary>
/// Minimal API endpoint handlers. Each handler calls the server-side
/// <see cref="IFileProvider"/> (S3) and returns a DTO to the WASM client.
/// Credentials never leave the server.
/// </summary>
internal static class FileEndpoints
{
    // ── GET /api/files/volumes ────────────────────────────────────────────────

    public static async Task<IResult> GetVolumes(
        IFileProvider provider,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("FAST.FileManager.Api.FileEndpoints");
        var result = await provider.GetVolumesAsync(ct);
        if (result.Failed)
        {
            logger.LogError("GetVolumes failed: {Error} — {Message} — Exception: {Ex}",
                result.Error, result.Message, result.Exception?.Message);
            return Results.Problem(
                detail: result.Message ?? "Could not load volumes.",
                title: result.Error.ToString());
        }

        var dtos = result.Value!.Select(DtoMapper.ToDto).ToList();
        return Results.Ok(dtos);
    }

    // ── GET /api/files/list?volumeId=x&segments=a&segments=b ─────────────────

    public static async Task<IResult> List(
        IFileProvider provider,
        ILoggerFactory loggerFactory,
        [FromQuery] string volumeId,
        [FromQuery] string[]? segments,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("FAST.FileManager.Api.FileEndpoints");
        var key = new StructuredKey(volumeId, segments ?? Array.Empty<string>());
        var result = await provider.ListAsync(key, ct);

        if (result.Failed)
        {
            logger.LogError("List failed for {Key}: {Error} — {Message} — Exception: {Ex}",
                key.ToString(), result.Error, result.Message, result.Exception?.Message);
            return Results.Problem(
                detail: result.Message ?? "Could not list folder.",
                title: result.Error.ToString());
        }

        var dtos = result.Value!.Select(DtoMapper.ToDto).ToList();
        return Results.Ok(dtos);
    }

    // ── POST /api/files/folder ────────────────────────────────────────────────

    public static async Task<IResult> CreateFolder(
        IFileProvider provider,
        [FromBody] CreateFolderRequest req,
        CancellationToken ct)
    {
        var parent = DtoMapper.KeyFromDto(req.VolumeId, req.ParentSegments);
        var result = await provider.CreateFolderAsync(parent, req.Name, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto(result.Value!))
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── DELETE /api/files/item ────────────────────────────────────────────────

    public static async Task<IResult> Delete(
        IFileProvider provider,
        [FromBody] DeleteRequest req,
        CancellationToken ct)
    {
        var item = DtoMapper.FromDto(req.Item);
        var result = await provider.DeleteAsync(item, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto())
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── PUT /api/files/rename ─────────────────────────────────────────────────

    public static async Task<IResult> Rename(
        IFileProvider provider,
        [FromBody] RenameRequest req,
        CancellationToken ct)
    {
        var item = DtoMapper.FromDto(req.Item);
        var result = await provider.RenameAsync(item, req.NewName, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto(result.Value!))
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── PUT /api/files/move ───────────────────────────────────────────────────

    public static async Task<IResult> Move(
        IFileProvider provider,
        [FromBody] MoveRequest req,
        CancellationToken ct)
    {
        var item   = DtoMapper.FromDto(req.Item);
        var target = DtoMapper.KeyFromDto(req.TargetVolumeId, req.TargetSegments);
        var result = await provider.MoveAsync(item, target, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto(result.Value!))
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── PUT /api/files/copy ───────────────────────────────────────────────────

    public static async Task<IResult> Copy(
        IFileProvider provider,
        [FromBody] CopyRequest req,
        CancellationToken ct)
    {
        var item   = DtoMapper.FromDto(req.Item);
        var target = DtoMapper.KeyFromDto(req.TargetVolumeId, req.TargetSegments);
        var result = await provider.CopyAsync(item, target, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto(result.Value!))
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── PUT /api/files/duplicate ──────────────────────────────────────────────

    public static async Task<IResult> Duplicate(
        IFileProvider provider,
        [FromBody] DuplicateRequest req,
        CancellationToken ct)
    {
        var item = DtoMapper.FromDto(req.Item);
        var result = await provider.DuplicateAsync(item, req.NewName, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto(result.Value!))
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── POST /api/files/upload ────────────────────────────────────────────────

    public static async Task<IResult> Upload(
        IFileProvider provider,
        IFormFile file,
        [FromForm] string volumeId,
        [FromForm] string segments,
        CancellationToken ct)
    {
        // segments is JSON-encoded array from the client
        var segs = System.Text.Json.JsonSerializer
            .Deserialize<string[]>(segments) ?? Array.Empty<string>();

        var targetFolder = new StructuredKey(volumeId, segs);

        await using var stream = file.OpenReadStream();
        var result = await provider.UploadAsync(
            targetFolder, file.FileName, stream, file.ContentType, ct);

        return result.Success
            ? Results.Ok(DtoMapper.OkDto(result.Value!))
            : Results.Ok(DtoMapper.FailDto(result));
    }

    // ── GET /api/files/download?volumeId=x&segments=a&fileReference=y ────────

    public static async Task<IResult> Download(
        IFileProvider provider,
        [FromQuery] string volumeId,
        [FromQuery] string[]? segments,
        [FromQuery] string? fileReference,
        [FromQuery] string? fileName,
        [FromQuery] string? contentType,
        CancellationToken ct)
    {
        var key = new StructuredKey(volumeId, segments ?? Array.Empty<string>());

        // Reconstruct a minimal StorageItem so the provider can find the file.
        var item = new StorageItem(StorageItemKind.File, key, fileName ?? "download")
        {
            FileReference = fileReference,
            ContentType   = contentType,
        };

        var result = await provider.DownloadAsync(item, ct);
        if (result.Failed)
            return Results.Problem(result.Message ?? "Download failed.");

        var stream = result.Value!;
        var mime   = contentType ?? "application/octet-stream";
        var name   = fileName ?? "download";

        return Results.Stream(stream, mime, name, enableRangeProcessing: false);
    }
}
