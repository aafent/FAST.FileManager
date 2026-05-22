using System.Net.Http.Json;
using System.Text.Json;
using FAST.FileManager.Abstractions;

namespace FAST.FileManager.App.Provider;

/// <summary>
/// An <see cref="IFileProvider"/> that calls the FileManager.Api backend.
/// Used in the Blazor WASM client — all S3 credentials stay on the server.
/// </summary>
public sealed class ApiFileProvider : IFileProvider
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ApiFileProvider(HttpClient http)
    {
        _http = http;
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public Capabilities GetCapabilities() => Capabilities.Full;

    // ── Volumes ───────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<IReadOnlyList<Volume>>> GetVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dtos = await _http.GetFromJsonAsync<List<VolumeDto>>(
                "/api/files/volumes", JsonOpts, cancellationToken);

            var volumes = (dtos ?? new())
                .Select(d => new Volume(d.Id, d.DisplayName))
                .ToList();

            return FileOperationResult<IReadOnlyList<Volume>>.Ok(volumes);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<Volume>>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<IReadOnlyList<StorageItem>>> ListAsync(
        StructuredKey folder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildListUrl(folder);
            var dtos = await _http.GetFromJsonAsync<List<StorageItemDto>>(
                url, JsonOpts, cancellationToken);

            var items = (dtos ?? new())
                .Select(FromDto)
                .ToList();

            return FileOperationResult<IReadOnlyList<StorageItem>>.Ok(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<StorageItem>>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── CreateFolder ──────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> CreateFolderAsync(
        StructuredKey parent, string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new CreateFolderRequest(parent.VolumeId, parent.Segments.ToArray(), name);
            var response = await _http.PostAsJsonAsync("/api/files/folder", req, cancellationToken);
            return await ReadItemResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult> DeleteAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new DeleteRequest(ToDto(item));
            var response = await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Delete, "/api/files/item")
                {
                    Content = JsonContent.Create(req)
                }, cancellationToken);

            return await ReadBaseResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult.Fail(FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> RenameAsync(
        StorageItem item, string newName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new RenameRequest(ToDto(item), newName);
            var response = await _http.PutAsJsonAsync("/api/files/rename", req, cancellationToken);
            return await ReadItemResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> MoveAsync(
        StorageItem item, StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new MoveRequest(
                ToDto(item), targetFolder.VolumeId, targetFolder.Segments.ToArray());
            var response = await _http.PutAsJsonAsync("/api/files/move", req, cancellationToken);
            return await ReadItemResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> CopyAsync(
        StorageItem item, StructuredKey targetFolder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new CopyRequest(
                ToDto(item), targetFolder.VolumeId, targetFolder.Segments.ToArray());
            var response = await _http.PutAsJsonAsync("/api/files/copy", req, cancellationToken);
            return await ReadItemResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> DuplicateAsync(
        StorageItem item,
        string newName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new DuplicateRequest(ToDto(item), newName);
            var response = await _http.PutAsJsonAsync("/api/files/duplicate", req, cancellationToken);
            return await ReadItemResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<StorageItem>> UploadAsync(
        StructuredKey targetFolder, string name, Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(targetFolder.VolumeId), "volumeId");
            form.Add(new StringContent(
                JsonSerializer.Serialize(targetFolder.Segments.ToArray())), "segments");

            var fileContent = new StreamContent(content);
            if (!string.IsNullOrEmpty(contentType))
                fileContent.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            form.Add(fileContent, "file", name);

            var response = await _http.PostAsync("/api/files/upload", form, cancellationToken);
            return await ReadItemResult(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return FileOperationResult<StorageItem>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public async Task<FileOperationResult<Stream>> DownloadAsync(
        StorageItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildDownloadUrl(item);
            var response = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return FileOperationResult<Stream>.Fail(
                    FileOperationError.Unknown,
                    $"Download failed: {(int)response.StatusCode}");

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return FileOperationResult<Stream>.Ok(stream);
        }
        catch (Exception ex)
        {
            return FileOperationResult<Stream>.Fail(
                FileOperationError.Unknown, ex.Message, ex);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string BuildListUrl(StructuredKey folder)
    {
        var sb = new System.Text.StringBuilder("/api/files/list?volumeId=");
        sb.Append(Uri.EscapeDataString(folder.VolumeId));
        foreach (var seg in folder.Segments)
        {
            sb.Append("&segments=");
            sb.Append(Uri.EscapeDataString(seg));
        }
        return sb.ToString();
    }

    private static string BuildDownloadUrl(StorageItem item)
    {
        var sb = new System.Text.StringBuilder("/api/files/download?volumeId=");
        sb.Append(Uri.EscapeDataString(item.Key.VolumeId));
        foreach (var seg in item.Key.Segments)
        {
            sb.Append("&segments=");
            sb.Append(Uri.EscapeDataString(seg));
        }
        if (item.FileReference is not null)
        {
            sb.Append("&fileReference=");
            sb.Append(Uri.EscapeDataString(item.FileReference.ToString()!));
        }
        sb.Append("&fileName=");
        sb.Append(Uri.EscapeDataString(item.Name));
        if (!string.IsNullOrEmpty(item.ContentType))
        {
            sb.Append("&contentType=");
            sb.Append(Uri.EscapeDataString(item.ContentType));
        }
        return sb.ToString();
    }

    private static async Task<FileOperationResult<StorageItem>> ReadItemResult(
        HttpResponseMessage response, CancellationToken ct)
    {
        var dto = await response.Content.ReadFromJsonAsync<OperationResultDto>(
            JsonOpts, ct);
        if (dto is null || !dto.Success)
        {
            var error = dto?.Error is not null
                ? Enum.TryParse<FileOperationError>(dto.Error, out var e)
                    ? e : FileOperationError.Unknown
                : FileOperationError.Unknown;
            return FileOperationResult<StorageItem>.Fail(error, dto?.Message);
        }
        return FileOperationResult<StorageItem>.Ok(FromDto(dto.Item!), dto.FileReference);
    }

    private static async Task<FileOperationResult> ReadBaseResult(
        HttpResponseMessage response, CancellationToken ct)
    {
        var dto = await response.Content.ReadFromJsonAsync<OperationResultDto>(
            JsonOpts, ct);
        if (dto is null || !dto.Success)
        {
            var error = dto?.Error is not null
                ? Enum.TryParse<FileOperationError>(dto.Error, out var e)
                    ? e : FileOperationError.Unknown
                : FileOperationError.Unknown;
            return FileOperationResult.Fail(error, dto?.Message);
        }
        return FileOperationResult.Ok();
    }

    private static StorageItemDto ToDto(StorageItem item) =>
        new(
            item.Kind.ToString(),
            item.Key.VolumeId,
            item.Key.Segments.ToArray(),
            item.Name, item.Title,
            item.Size, item.LastModified,
            item.ContentType, item.Owner,
            item.ReadOnly,
            item.Tag?.ToString(),
            item.AppReference?.ToString(),
            item.FileReference?.ToString()
        );

    private static StorageItem FromDto(StorageItemDto dto)
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
}
