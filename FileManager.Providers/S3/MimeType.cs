using FAST.FileManager.Providers;

namespace FAST.FileManager.Providers.S3;

/// <summary>
/// Forwards to the shared <see cref="MimeTypeHelper"/> at the Providers level.
/// Kept for backward compatibility within the S3 provider.
/// </summary>
internal static class MimeType
{
    public static string Guess(string fileName) =>
        MimeTypeHelper.Guess(fileName);
}
