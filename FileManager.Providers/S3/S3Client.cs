using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace FAST.FileManager.Providers.S3;

/// <summary>
/// A thin, hand-rolled S3 REST client. Implements only the seven operations
/// needed by the file manager provider:
/// <list type="bullet">
///   <item><description>ListBuckets</description></item>
///   <item><description>ListObjectsV2  (with prefix + delimiter for folder emulation)</description></item>
///   <item><description>HeadObject</description></item>
///   <item><description>PutObject      (upload and folder-marker creation)</description></item>
///   <item><description>GetObject      (download)</description></item>
///   <item><description>DeleteObject</description></item>
///   <item><description>CopyObject     (via PUT with x-amz-copy-source)</description></item>
/// </list>
/// Uses path-style addressing: <c>{endpoint}/{bucket}/{key}</c>.
/// Payload signing uses UNSIGNED-PAYLOAD for PutObject; all other operations
/// hash an empty or small, fully-buffered body.
/// </summary>
public sealed class S3Client
{
    private readonly HttpClient _http;
    private readonly SigV4Signer _signer;
    private readonly string _endpoint;
    private readonly bool _virtualHostedStyle;

    public S3Client(HttpClient http, SigV4Signer signer, string endpoint, bool virtualHostedStyle = false)
    {
        _http = http;
        _signer = signer;
        _endpoint = endpoint.TrimEnd('/');
        _virtualHostedStyle = virtualHostedStyle;
    }

    /// <summary>
    /// Builds the base URL for a bucket.
    /// Path-style:          https://account.r2.cloudflarestorage.com/bucket
    /// Virtual-hosted-style: https://bucket.account.r2.cloudflarestorage.com
    /// </summary>
    private string BucketUrl(string bucket)
    {
        if (!_virtualHostedStyle)
            return $"{_endpoint}/{bucket}";

        // Insert bucket name as subdomain of the endpoint host.
        var uri = new Uri(_endpoint);
        return $"{uri.Scheme}://{bucket}.{uri.Host}";
    }

    // ── ListBuckets ──────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all buckets accessible with the configured credentials.
    /// Maps to the <c>GET /</c> S3 operation.
    /// </summary>
    public async Task<List<S3Bucket>> ListBucketsAsync(CancellationToken ct)
    {
        var uri = new Uri($"{_endpoint}/");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        _signer.Sign(request);

        var response = await _http.SendAsync(request, ct);
        var xml = await ReadXmlAsync(response, ct);

        // <ListAllMyBucketsResult>
        //   <Buckets>
        //     <Bucket><Name>…</Name><CreationDate>…</CreationDate></Bucket>
        //   </Buckets>
        // </ListAllMyBucketsResult>
        var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
        return xml.Descendants(ns + "Bucket")
            .Select(b => new S3Bucket(
                Name: b.Element(ns + "Name")?.Value ?? string.Empty,
                CreationDate: b.Element(ns + "CreationDate")?.Value))
            .Where(b => !string.IsNullOrEmpty(b.Name))
            .ToList();
    }

    // ── ListObjectsV2 ────────────────────────────────────────────────────────

    /// <summary>
    /// Lists objects within a bucket under the given prefix (folder).
    /// Uses the "/" delimiter so that sub-folders appear as CommonPrefixes
    /// rather than individual keys, emulating a folder hierarchy.
    /// Automatically pages through all continuation tokens.
    /// </summary>
    /// <param name="bucket">The bucket name.</param>
    /// <param name="prefix">
    /// The key prefix to list under. Use empty string for the bucket root.
    /// Must end with "/" for a folder, or be empty.
    /// </param>
    public async Task<S3ListResult> ListObjectsAsync(
        string bucket,
        string prefix,
        CancellationToken ct)
    {
        var files = new List<S3Object>();
        var folders = new List<string>();
        string? continuationToken = null;

        do
        {
            var query = BuildListQuery(prefix, continuationToken);
            var uri = new Uri($"{BucketUrl(bucket)}?{query}");
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            _signer.Sign(request);

            var response = await _http.SendAsync(request, ct);
            var xml = await ReadXmlAsync(response, ct);

            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;

            // Files: <Contents> elements
            foreach (var content in xml.Descendants(ns + "Contents"))
            {
                var key = content.Element(ns + "Key")?.Value ?? string.Empty;
                // Skip the folder marker object itself (key ends with /)
                if (key.EndsWith('/'))
                    continue;

                files.Add(new S3Object(
                    Key: key,
                    Size: long.TryParse(content.Element(ns + "Size")?.Value, out var size) ? size : 0,
                    LastModified: content.Element(ns + "LastModified")?.Value,
                    ETag: content.Element(ns + "ETag")?.Value?.Trim('"'),
                    Owner: content.Element(ns + "Owner")
                                  ?.Element(ns + "DisplayName")?.Value));
            }

            // Folders: <CommonPrefixes> elements
            foreach (var cp in xml.Descendants(ns + "CommonPrefixes"))
            {
                var folderPrefix = cp.Element(ns + "Prefix")?.Value;
                if (!string.IsNullOrEmpty(folderPrefix))
                    folders.Add(folderPrefix);
            }

            // Pagination
            var isTruncated = string.Equals(
                xml.Root?.Element(ns + "IsTruncated")?.Value,
                "true", StringComparison.OrdinalIgnoreCase);

            continuationToken = isTruncated
                ? xml.Root?.Element(ns + "NextContinuationToken")?.Value
                : null;

        } while (continuationToken is not null);

        return new S3ListResult(files, folders);
    }

    private static string BuildListQuery(string prefix, string? continuationToken)
    {
        // Note: do NOT pre-encode values here — the SigV4 signer's canonical
        // query string builder will encode them correctly. Pre-encoding causes
        // double-encoding and signature mismatch.
        var sb = new StringBuilder("delimiter=/&list-type=2");

        if (!string.IsNullOrEmpty(prefix))
        {
            sb.Append("&prefix=");
            sb.Append(prefix);
        }

        if (continuationToken is not null)
        {
            sb.Append("&continuation-token=");
            sb.Append(continuationToken);
        }

        return sb.ToString();
    }

    // ── HeadObject ────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves metadata for a single object without downloading its content.
    /// Returns null when the object does not exist (404).
    /// </summary>
    public async Task<S3ObjectMeta?> HeadObjectAsync(
        string bucket, string key, CancellationToken ct)
    {
        var uri = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Head, uri);
        _signer.Sign(request);

        var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return new S3ObjectMeta(
            ContentType: response.Content.Headers.ContentType?.MediaType,
            ContentLength: response.Content.Headers.ContentLength ?? 0,
            LastModified: response.Content.Headers.LastModified,
            ETag: response.Headers.ETag?.Tag?.Trim('"'));
    }

    // ── PutObject ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads an object. Uses UNSIGNED-PAYLOAD to avoid buffering.
    /// </summary>
    public async Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType,
        CancellationToken ct)
    {
        var uri = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StreamContent(content)
        };

        if (!string.IsNullOrEmpty(contentType))
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        // UNSIGNED-PAYLOAD: avoids reading the entire stream to compute SHA256.
        _signer.Sign(request, unsignedPayload: true);

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a zero-byte folder marker object. The key must end with "/".
    /// </summary>
    public async Task PutFolderMarkerAsync(
        string bucket, string key, CancellationToken ct)
    {
        if (!key.EndsWith('/'))
            key += '/';

        var uri = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Content.Headers.ContentType =
            MediaTypeHeaderValue.Parse("application/x-directory");

        _signer.Sign(request);

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    // ── GetObject ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a readable stream for the object's content.
    /// The caller owns and must dispose the returned stream.
    /// </summary>
    public async Task<Stream> GetObjectAsync(
        string bucket, string key, CancellationToken ct)
    {
        var uri = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        _signer.Sign(request);

        // ResponseHeadersRead: don't buffer the body — stream it.
        var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    // ── DeleteObject ──────────────────────────────────────────────────────────

    /// <summary>Deletes a single object by key.</summary>
    public async Task DeleteObjectAsync(
        string bucket, string key, CancellationToken ct)
    {
        var uri = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        _signer.Sign(request);

        var response = await _http.SendAsync(request, ct);
        // 204 No Content is the normal success response for DELETE.
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
    }

    // ── CopyObject ────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies an object from <paramref name="sourceKey"/> to
    /// <paramref name="destKey"/> within the same bucket.
    /// </summary>
    public async Task CopyObjectAsync(
        string bucket,
        string sourceKey,
        string destKey,
        CancellationToken ct)
    {
        var uri = new Uri($"{BucketUrl(bucket)}/{EscapeKey(destKey)}");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Content.Headers.ContentLength = 0;
        // Explicitly clear Content-Type — CopyObject has no body and
        // .NET may auto-set application/octet-stream which would be
        // included in SignedHeaders and cause SignatureDoesNotMatch.
        request.Content.Headers.ContentType = null;

        // x-amz-copy-source: path-style /bucket/key with key segments encoded.
        // Must be set before signing so it is included in SignedHeaders.
        var copySource = $"/{bucket}/{EscapeKey(sourceKey)}";
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource);

        _signer.Sign(request);

        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string? code = null, message = null;
            try
            {
                var xml = System.Xml.Linq.XDocument.Parse(body);
                var ns  = xml.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                code    = xml.Root?.Element(ns + "Code")?.Value;
                message = xml.Root?.Element(ns + "Message")?.Value;
            }
            catch { /* ignore parse errors */ }
            throw new S3Exception(
                (int)response.StatusCode,
                code ?? response.StatusCode.ToString(),
                message ?? body);
        }
    }

    // ── Shared utilities ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the response body as XML, throwing an
    /// <see cref="S3Exception"/> on non-success status codes.
    /// </summary>
    private static async Task<XDocument> ReadXmlAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Parse the S3 error response to get Code and Message.
            string? code = null;
            string? message = null;
            try
            {
                var errorXml = XDocument.Parse(body);
                var ns = errorXml.Root?.Name.Namespace ?? XNamespace.None;
                code = errorXml.Root?.Element(ns + "Code")?.Value;
                message = errorXml.Root?.Element(ns + "Message")?.Value;
            }
            catch { /* ignore parse errors; use raw body below */ }

            throw new S3Exception(
                (int)response.StatusCode,
                code ?? response.StatusCode.ToString(),
                message ?? body);
        }

        return XDocument.Parse(body);
    }

    /// <summary>
    /// URL-encodes an S3 key for use in a path segment.
    /// Preserves "/" separators so paths are not double-encoded.
    /// </summary>
    private static string EscapeKey(string key)
    {
        // Encode each segment separately to preserve / structure.
        return string.Join('/',
            key.Split('/').Select(Uri.EscapeDataString));
    }
}

// ── Supporting data records ───────────────────────────────────────────────────

public record S3Bucket(string Name, string? CreationDate);

public record S3Object(
    string Key,
    long Size,
    string? LastModified,
    string? ETag,
    string? Owner);

public record S3ObjectMeta(
    string? ContentType,
    long ContentLength,
    DateTimeOffset? LastModified,
    string? ETag);

public record S3ListResult(
    List<S3Object> Files,
    List<string> FolderPrefixes);

/// <summary>
/// Thrown by <see cref="S3Client"/> when the S3 server returns a non-success
/// response. Caught by <see cref="S3FileProvider"/> and translated into a
/// <see cref="FAST.FileManager.Abstractions.FileOperationResult"/>.
/// </summary>
public sealed class S3Exception : Exception
{
    public S3Exception(int statusCode, string s3Code, string s3Message)
        : base($"S3 error {statusCode} ({s3Code}): {s3Message}")
    {
        StatusCode = statusCode;
        S3Code = s3Code;
        S3Message = s3Message;
    }

    public int StatusCode { get; }
    public string S3Code { get; }
    public string S3Message { get; }
}
