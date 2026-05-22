namespace FAST.FileManager.Providers;

/// <summary>
/// Provides a minimal, dependency-free extension-to-MIME-type mapping.
/// Used when the S3 ListObjectsV2 response does not return a content type
/// (which is normal — S3 only returns content-type on HeadObject/GetObject).
/// </summary>
public static class MimeTypeHelper
{
    private static readonly Dictionary<string, string> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Text
            { ".txt",  "text/plain" },
            { ".csv",  "text/csv" },
            { ".html", "text/html" },
            { ".htm",  "text/html" },
            { ".css",  "text/css" },
            { ".md",   "text/markdown" },
            { ".xml",  "application/xml" },
            { ".json", "application/json" },
            { ".js",   "application/javascript" },
            { ".ts",   "application/typescript" },
            // Images
            { ".png",  "image/png" },
            { ".jpg",  "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".gif",  "image/gif" },
            { ".svg",  "image/svg+xml" },
            { ".webp", "image/webp" },
            { ".ico",  "image/x-icon" },
            // Documents
            { ".pdf",  "application/pdf" },
            { ".doc",  "application/msword" },
            { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { ".xls",  "application/vnd.ms-excel" },
            { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            { ".ppt",  "application/vnd.ms-powerpoint" },
            { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
            // Archives
            { ".zip",  "application/zip" },
            { ".tar",  "application/x-tar" },
            { ".gz",   "application/gzip" },
            { ".7z",   "application/x-7z-compressed" },
            // Audio / Video
            { ".mp3",  "audio/mpeg" },
            { ".wav",  "audio/wav" },
            { ".mp4",  "video/mp4" },
            { ".webm", "video/webm" },
            // Data / Code
            { ".sql",  "application/sql" },
            { ".yaml", "application/yaml" },
            { ".yml",  "application/yaml" },
            { ".cs",   "text/plain" },
            { ".razor","text/plain" },
        };

    /// <summary>
    /// Returns the MIME type for the given file name or extension.
    /// Returns <c>application/octet-stream</c> for unknown extensions.
    /// </summary>
    public static string Guess(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext)
            ? "application/octet-stream"
            : Map.GetValueOrDefault(ext, "application/octet-stream");
    }
}
