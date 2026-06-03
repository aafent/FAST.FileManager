namespace FAST.FileManager.Providers.S3;

/// <summary>
/// Configuration options for the S3-compatible file provider.
/// Bind this from <c>appsettings.json</c> using
/// <c>builder.Services.Configure&lt;S3ProviderOptions&gt;(builder.Configuration.GetSection("S3"))</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security notice:</b> In a Blazor WASM application, <c>appsettings.json</c>
/// is downloaded to and fully readable by the browser. The
/// <see cref="SecretKey"/> is therefore visible to any user of the app.
/// This configuration model is intentional for development and
/// trusted-user scenarios only. A future auth refactor will move credential
/// handling to a backend or presigned-URL strategy.
/// </para>
/// <para>
/// The provider uses path-style addressing: <c>{Endpoint}/{Bucket}/{Key}</c>.
/// Virtual-hosted-style addressing is not supported in v1.
/// </para>
/// </remarks>
public sealed class S3ProviderOptions
{
    /// <summary>The default configuration section name.</summary>
    public const string DefaultSectionName = "S3";

    /// <summary>
    /// The base URL of the S3-compatible endpoint, without a trailing slash.
    /// Examples:
    /// <list type="bullet">
    ///   <item><description><c>http://localhost:9000</c> (local MinIO)</description></item>
    ///   <item><description><c>https://s3.eu-west-1.amazonaws.com</c> (AWS S3)</description></item>
    ///   <item><description><c>https://minio.mycompany.com</c> (hosted MinIO)</description></item>
    /// </list>
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The AWS region or S3-compatible region string.
    /// For MinIO this is typically <c>us-east-1</c> unless configured otherwise.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>The access key id (equivalent to a username).</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// The secret access key. See the security notice on
    /// <see cref="S3ProviderOptions"/>: this value is exposed in the browser
    /// when used from Blazor WASM.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// An explicit list of bucket names to expose as volumes. When this list
    /// is non-empty the provider uses it directly and never calls ListBuckets.
    /// Recommended for Cloudflare R2 where the ListBuckets endpoint may not
    /// be reachable from all network environments.
    /// When empty the provider falls back to calling ListBuckets.
    /// </summary>
    public List<string> Buckets { get; set; } = new();

    /// <summary>
    /// When true, uses virtual-hosted-style URLs.
    /// </summary>
    public bool VirtualHostedStyle { get; set; } = false;

    /// <summary>
    /// When true, the provider will use HTTP instead of HTTPS.
    /// </summary>
    public bool UseHttp { get; set; } = false;

    /// <summary>
    /// When true, all requests are authenticated with a simple Bearer token
    /// instead of AWS Signature Version 4 (SigV4).
    /// The <see cref="SecretKey"/> is used verbatim as the token:
    /// <c>Authorization: Bearer {SecretKey}</c>.
    /// Use this for S3-compatible endpoints that implement FAST token auth
    /// rather than SigV4 signing (e.g. FAST.FileRepository).
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool UseBearerAuth { get; set; } = false;
}
