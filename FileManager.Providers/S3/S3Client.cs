using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <remarks>
/// <para><b>Logging levels:</b></para>
/// <list type="bullet">
///   <item><description>
///     <b>Debug</b> — every outgoing request: method + full URL + response
///     status code. Useful for diagnosing which operations fail against a
///     specific S3-compatible service.
///   </description></item>
///   <item><description>
///     <b>Trace</b> — all Debug output plus request/response headers and
///     a <c>.http</c> file snippet you can copy-paste into VS / Rider /
///     VS Code REST Client to replay the exact request (valid within the
///     SigV4 15-minute window).
///   </description></item>
/// </list>
/// Set the log level in <c>appsettings.json</c>:
/// <code>
/// "Logging": {
///   "LogLevel": {
///     "FAST.FileManager.Providers.S3.S3Client": "Trace"
///   }
/// }
/// </code>
/// </remarks>
public sealed class S3Client
{
    private readonly HttpClient    _http;
    private readonly SigV4Signer   _signer;
    private readonly string        _endpoint;
    private readonly bool          _virtualHostedStyle;
    private readonly ILogger<S3Client> _logger;

    public S3Client(
        HttpClient http,
        SigV4Signer signer,
        string endpoint,
        bool virtualHostedStyle = false,
        ILogger<S3Client>? logger = null)
    {
        _http               = http;
        _signer             = signer;
        _endpoint           = endpoint.TrimEnd('/');
        _virtualHostedStyle = virtualHostedStyle;
        _logger             = logger ?? NullLogger<S3Client>.Instance;
    }

    /// <summary>
    /// Builds the base URL for a bucket.
    /// Path-style:           https://account.r2.cloudflarestorage.com/bucket
    /// Virtual-hosted-style: https://bucket.account.r2.cloudflarestorage.com
    /// </summary>
    private string BucketUrl(string bucket)
    {
        if (!_virtualHostedStyle)
            return $"{_endpoint}/{bucket}";

        var uri = new Uri(_endpoint);
        return $"{uri.Scheme}://{bucket}.{uri.Host}";
    }

    // ── ListBuckets ──────────────────────────────────────────────────────────

    public async Task<List<S3Bucket>> ListBucketsAsync(CancellationToken ct)
    {
        var uri     = new Uri($"{_endpoint}/");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        _signer.Sign(request);

        var response = await SendAsync(request, ct);
        var xml      = await ReadXmlAsync(response, ct);

        var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
        return xml.Descendants(ns + "Bucket")
            .Select(b => new S3Bucket(
                Name:         b.Element(ns + "Name")?.Value ?? string.Empty,
                CreationDate: b.Element(ns + "CreationDate")?.Value))
            .Where(b => !string.IsNullOrEmpty(b.Name))
            .ToList();
    }

    // ── ListObjectsV2 ────────────────────────────────────────────────────────

    public async Task<S3ListResult> ListObjectsAsync(
        string bucket,
        string prefix,
        CancellationToken ct)
    {
        var files   = new List<S3Object>();
        var folders = new List<string>();
        string? continuationToken = null;

        do
        {
            var query   = BuildListQuery(prefix, continuationToken);
            var uri     = new Uri($"{BucketUrl(bucket)}?{query}");
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            _signer.Sign(request);

            var response = await SendAsync(request, ct);
            var xml      = await ReadXmlAsync(response, ct);

            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;

            foreach (var content in xml.Descendants(ns + "Contents"))
            {
                var key = content.Element(ns + "Key")?.Value ?? string.Empty;
                if (key.EndsWith('/')) continue;

                files.Add(new S3Object(
                    Key:          key,
                    Size:         long.TryParse(content.Element(ns + "Size")?.Value, out var size) ? size : 0,
                    LastModified: content.Element(ns + "LastModified")?.Value,
                    ETag:         content.Element(ns + "ETag")?.Value?.Trim('"'),
                    Owner:        content.Element(ns + "Owner")?.Element(ns + "DisplayName")?.Value));
            }

            foreach (var cp in xml.Descendants(ns + "CommonPrefixes"))
            {
                var folderPrefix = cp.Element(ns + "Prefix")?.Value;
                if (!string.IsNullOrEmpty(folderPrefix))
                    folders.Add(folderPrefix);
            }

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

    public async Task<S3ObjectMeta?> HeadObjectAsync(
        string bucket, string key, CancellationToken ct)
    {
        var uri     = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Head, uri);
        _signer.Sign(request);

        var response = await SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return new S3ObjectMeta(
            ContentType:   response.Content.Headers.ContentType?.MediaType,
            ContentLength: response.Content.Headers.ContentLength ?? 0,
            LastModified:  response.Content.Headers.LastModified,
            ETag:          response.Headers.ETag?.Tag?.Trim('"'));
    }

    // ── PutObject ─────────────────────────────────────────────────────────────

    public async Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType,
        CancellationToken ct)
    {
        var uri     = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StreamContent(content)
        };

        if (!string.IsNullOrEmpty(contentType))
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        _signer.Sign(request, unsignedPayload: true);

        var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task PutFolderMarkerAsync(
        string bucket, string key, CancellationToken ct)
    {
        if (!key.EndsWith('/')) key += '/';

        var uri     = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Content.Headers.ContentType =
            MediaTypeHeaderValue.Parse("application/x-directory");

        _signer.Sign(request);

        var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    // ── GetObject ─────────────────────────────────────────────────────────────

    public async Task<Stream> GetObjectAsync(
        string bucket, string key, CancellationToken ct)
    {
        var uri     = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        _signer.Sign(request);

        var response = await SendAsync(
            request,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    // ── DeleteObject ──────────────────────────────────────────────────────────

    public async Task DeleteObjectAsync(
        string bucket, string key, CancellationToken ct)
    {
        var uri     = new Uri($"{BucketUrl(bucket)}/{EscapeKey(key)}");
        var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        _signer.Sign(request);

        var response = await SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
    }

    // ── CopyObject ────────────────────────────────────────────────────────────

    public async Task CopyObjectAsync(
        string bucket,
        string sourceKey,
        string destKey,
        CancellationToken ct)
    {
        var uri     = new Uri($"{BucketUrl(bucket)}/{EscapeKey(destKey)}");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Content.Headers.ContentLength = 0;
        request.Content.Headers.ContentType   = null;

        var copySource = $"/{bucket}/{EscapeKey(sourceKey)}";
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource);

        _signer.Sign(request);

        var response = await SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string? code = null, message = null;
            try
            {
                var xml = XDocument.Parse(body);
                var ns  = xml.Root?.Name.Namespace ?? XNamespace.None;
                code    = xml.Root?.Element(ns + "Code")?.Value;
                message = xml.Root?.Element(ns + "Message")?.Value;
            }
            catch { /* ignore parse errors */ }
            throw new S3Exception(
                (int)response.StatusCode,
                code    ?? response.StatusCode.ToString(),
                message ?? body);
        }
    }

    // ── Core send + logging ───────────────────────────────────────────────────

    /// <summary>
    /// Sends a signed HTTP request, logging at Debug and Trace levels.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        // ── Trace: emit .http snippet BEFORE sending (signature is still valid) ──
        if (_logger.IsEnabled(LogLevel.Trace))
            LogHttpSnippet(request);

        // ── Debug: log method + URL ───────────────────────────────────────────
        _logger.LogDebug("S3 → {Method} {Url}", request.Method, request.RequestUri);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completionOption, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3 ← {Method} {Url} EXCEPTION",
                request.Method, request.RequestUri);
            throw;
        }

        // ── Debug: log status ─────────────────────────────────────────────────
        var level = response.IsSuccessStatusCode ? LogLevel.Debug : LogLevel.Warning;
        _logger.Log(level, "S3 ← {Method} {Url} {Status} {Reason}",
            request.Method,
            request.RequestUri,
            (int)response.StatusCode,
            response.ReasonPhrase);

        // ── Trace: log response headers ───────────────────────────────────────
        if (_logger.IsEnabled(LogLevel.Trace))
            LogResponseHeaders(response);

        return response;
    }

    /// <summary>
    /// Emits a <c>.http</c> file snippet to the Trace log.
    /// The snippet is valid within the SigV4 15-minute window — copy-paste
    /// it immediately into VS / Rider / VS Code REST Client to replay.
    /// </summary>
    private void LogHttpSnippet(HttpRequestMessage request)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("### .http snippet (valid ~15 min — copy to REST Client)");
        sb.AppendLine($"{request.Method} {request.RequestUri}");

        // Request headers (includes Authorization + x-amz-* signed headers)
        foreach (var h in request.Headers)
            foreach (var v in h.Value)
                sb.AppendLine($"{h.Key}: {v}");

        // Content headers
        if (request.Content is not null)
            foreach (var h in request.Content.Headers)
                foreach (var v in h.Value)
                    sb.AppendLine($"{h.Key}: {v}");

        sb.AppendLine();

        _logger.LogTrace("{HttpSnippet}", sb.ToString());
    }

    /// <summary>Logs response status line and headers at Trace level.</summary>
    private void LogResponseHeaders(HttpResponseMessage response)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"### Response: {(int)response.StatusCode} {response.ReasonPhrase}");

        foreach (var h in response.Headers)
            foreach (var v in h.Value)
                sb.AppendLine($"{h.Key}: {v}");

        foreach (var h in response.Content.Headers)
            foreach (var v in h.Value)
                sb.AppendLine($"{h.Key}: {v}");

        _logger.LogTrace("{ResponseHeaders}", sb.ToString());
    }

    // ── Shared utilities ──────────────────────────────────────────────────────

    private static async Task<XDocument> ReadXmlAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string? code = null, message = null;
            try
            {
                var errorXml = XDocument.Parse(body);
                var ns       = errorXml.Root?.Name.Namespace ?? XNamespace.None;
                code         = errorXml.Root?.Element(ns + "Code")?.Value;
                message      = errorXml.Root?.Element(ns + "Message")?.Value;
            }
            catch { /* ignore parse errors */ }

            throw new S3Exception(
                (int)response.StatusCode,
                code    ?? response.StatusCode.ToString(),
                message ?? body);
        }

        return XDocument.Parse(body);
    }

    private static string EscapeKey(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
}

// ── Supporting data records ───────────────────────────────────────────────────

public record S3Bucket(string Name, string? CreationDate);

public record S3Object(
    string  Key,
    long    Size,
    string? LastModified,
    string? ETag,
    string? Owner);

public record S3ObjectMeta(
    string?         ContentType,
    long            ContentLength,
    DateTimeOffset? LastModified,
    string?         ETag);

public record S3ListResult(
    List<S3Object> Files,
    List<string>   FolderPrefixes);

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
        S3Code     = s3Code;
        S3Message  = s3Message;
    }

    public int    StatusCode { get; }
    public string S3Code     { get; }
    public string S3Message  { get; }
}
