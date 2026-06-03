using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace FAST.FileManager.Providers.S3;

/// <summary>
/// Computes and applies AWS Signature Version 4 (SigV4) authentication
/// headers to an <see cref="HttpRequestMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the subset of SigV4 needed for the seven S3 operations used by the
/// file provider is implemented:
/// <list type="bullet">
///   <item><description>GET  (ListBuckets, ListObjectsV2, GetObject, HeadObject)</description></item>
///   <item><description>PUT  (PutObject, CreateFolder marker)</description></item>
///   <item><description>DELETE (DeleteObject)</description></item>
///   <item><description>COPY (via PUT with x-amz-copy-source header)</description></item>
/// </list>
/// </para>
/// <para>
/// Uploads use <c>UNSIGNED-PAYLOAD</c> to avoid reading the entire stream
/// into memory for hashing. All other operations hash an empty or small body.
/// </para>
/// <para>
/// Path-style addressing is assumed: <c>{endpoint}/{bucket}/{key}</c>.
/// </para>
/// </remarks>
public sealed class SigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "s3";
    private const string Terminator = "aws4_request";
    private const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly bool   _useBearerAuth;

    public SigV4Signer(string accessKey, string secretKey, string region, bool useBearerAuth = false)
    {
        _accessKey     = accessKey;
        _secretKey     = secretKey;
        _region        = region;
        _useBearerAuth = useBearerAuth;
    }

    /// <summary>
    /// Signs the given request by adding <c>Authorization</c>,
    /// <c>x-amz-date</c>, and <c>x-amz-content-sha256</c> headers.
    /// </summary>
    /// <param name="request">The request to sign. Must have an absolute URI.</param>
    /// <param name="unsignedPayload">
    /// When true (for uploads), uses UNSIGNED-PAYLOAD instead of hashing the
    /// body. This avoids reading the entire stream into memory.
    /// </param>
    /// <param name="overrideNow">
    /// Optional fixed timestamp, for testing. Null means use UTC now.
    /// </param>
    public void Sign(
        HttpRequestMessage request,
        bool unsignedPayload = false,
        DateTimeOffset? overrideNow = null)
    {
        // ── Bearer auth short-circuit ────────────────────────────────────────
        if (_useBearerAuth)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _secretKey);
            return;
        }

        var now = overrideNow ?? DateTimeOffset.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        // ── Step 1: payload hash ────────────────────────────────────────────
        var payloadHash = unsignedPayload
            ? UnsignedPayload
            : HashPayload(request);

        // ── Step 2: add required headers before canonical construction ──────
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        // Host must be in headers for the canonical request.
        if (!request.Headers.Contains("Host"))
        {
            var uri = request.RequestUri!;
            var host = uri.IsDefaultPort
                ? uri.Host
                : $"{uri.Host}:{uri.Port}";
            request.Headers.TryAddWithoutValidation("Host", host);
        }

        // ── Step 3: canonical request ───────────────────────────────────────
        var (signedHeaders, canonicalHeaders) = BuildCanonicalHeaders(request);
        var canonicalQueryString = BuildCanonicalQueryString(request.RequestUri!);
        var canonicalUri = BuildCanonicalUri(request.RequestUri!);

        var canonicalRequest = string.Join('\n',
            request.Method.Method.ToUpperInvariant(),
            canonicalUri,
            canonicalQueryString,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        // ── Step 4: string to sign ──────────────────────────────────────────
        var credentialScope = $"{dateStamp}/{_region}/{Service}/{Terminator}";
        var stringToSign = string.Join('\n',
            Algorithm,
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        // ── Step 5: signing key ─────────────────────────────────────────────
        var signingKey = DeriveSigningKey(dateStamp);

        // ── Step 6: signature ───────────────────────────────────────────────
        var signature = HmacHex(signingKey, stringToSign);

        // ── Step 7: Authorization header ────────────────────────────────────
        var authorization =
            $"{Algorithm} " +
            $"Credential={_accessKey}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, " +
            $"Signature={signature}";

        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private static string HashPayload(HttpRequestMessage request)
    {
        if (request.Content is null)
            return EmptyPayloadHash;

        // Read the body synchronously — only used for small, non-streaming
        // bodies (all operations except uploads).
        var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        return Sha256Hex(bytes);
    }

    /// <summary>
    /// Builds the canonical headers string and the signed-headers list.
    /// Only headers needed for signing are included: host, x-amz-*, content-type.
    /// </summary>
    private static (string SignedHeaders, string CanonicalHeaders) BuildCanonicalHeaders(
        HttpRequestMessage request)
    {
        // Collect headers to sign.
        var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string name)
        {
            if (request.Headers.TryGetValues(name, out var vals))
                headers[name.ToLowerInvariant()] = string.Join(',', vals).Trim();
        }

        TryAdd("Host");
        TryAdd("x-amz-date");
        TryAdd("x-amz-content-sha256");

        // Only include Content-Type when explicitly set (not auto-set by .NET).
        // For CopyObject (empty body, no content type set) this prevents
        // signing a header that R2 doesn't include in its own computation.
        if (request.Content?.Headers.ContentType is { } ct)
            headers["content-type"] = ct.ToString();

        // Pick up any additional x-amz-* headers set by the caller.
        foreach (var h in request.Headers)
        {
            var lower = h.Key.ToLowerInvariant();
            if (!lower.StartsWith("x-amz-") || headers.ContainsKey(lower))
                continue;
            headers[lower] = string.Join(',', h.Value).Trim();
        }

        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(
            headers.Select(kv => $"{kv.Key}:{kv.Value}\n"));

        return (signedHeaders, canonicalHeaders);
    }

    /// <summary>
    /// Builds the canonical URI: the path portion, URI-encoded, with double
    /// slashes collapsed. S3 path-style URIs look like /{bucket}/{key}.
    /// </summary>
    private static string BuildCanonicalUri(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
            return "/";

        // uri.AbsolutePath may be partially decoded by .NET's Uri class.
        // Unescape each segment fully first, then re-encode per SigV4 spec
        // to get a consistent canonical form regardless of how the Uri was built.
        var segments = path.Split('/');
        return string.Join('/', segments.Select(s =>
            UriEncode(Uri.UnescapeDataString(s))));
    }

    /// <summary>
    /// Builds the canonical query string: sorted by key, then by value,
    /// URI-encoded per SigV4 spec.
    /// </summary>
    private static string BuildCanonicalQueryString(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        // Strip leading '?'
        query = query.TrimStart('?');

        var pairs = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var idx = p.IndexOf('=');
                if (idx < 0)
                    return (Key: UriEncode(Uri.UnescapeDataString(p)), Value: string.Empty);

                // Decode first (in case Uri already percent-encoded some chars),
                // then re-encode per SigV4 spec to get a consistent canonical form.
                var rawKey   = Uri.UnescapeDataString(p[..idx]);
                var rawValue = Uri.UnescapeDataString(p[(idx + 1)..]);
                return (Key: UriEncode(rawKey), Value: UriEncode(rawValue));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>
    /// Derives the signing key:
    /// HMAC(HMAC(HMAC(HMAC("AWS4"+secret, date), region), service), "aws4_request")
    /// </summary>
    private byte[] DeriveSigningKey(string dateStamp)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + _secretKey);
        var kDate = HmacBytes(kSecret, dateStamp);
        var kRegion = HmacBytes(kDate, _region);
        var kService = HmacBytes(kRegion, Service);
        return HmacBytes(kService, Terminator);
    }

    // ── Crypto utilities ────────────────────────────────────────────────────

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] HmacBytes(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string HmacHex(byte[] key, string data)
        => Convert.ToHexString(HmacBytes(key, data)).ToLowerInvariant();

    /// <summary>
    /// URI-encodes a string per the SigV4 spec: encodes everything except
    /// unreserved characters (A-Z a-z 0-9 - _ . ~).
    /// </summary>
    private static string UriEncode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length * 3);
        var bytes = Encoding.UTF8.GetBytes(value);

        foreach (var b in bytes)
        {
            if ((b >= 'A' && b <= 'Z') ||
                (b >= 'a' && b <= 'z') ||
                (b >= '0' && b <= '9') ||
                b == '-' || b == '_' || b == '.' || b == '~')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%');
                sb.Append(Convert.ToHexString(new[] { b }));
            }
        }

        return sb.ToString();
    }
}
