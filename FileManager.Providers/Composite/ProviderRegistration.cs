using FAST.FileManager.Abstractions;

namespace FAST.FileManager.Providers.Composite;

/// <summary>
/// Pairs an <see cref="IFileProvider"/> with its alias for use in the
/// <see cref="CompositeFileProvider"/>.
/// </summary>
public sealed class ProviderRegistration
{
    public ProviderRegistration(IFileProvider provider, string alias)
    {
        Provider = provider;
        Alias    = alias;
    }

    public IFileProvider Provider { get; }
    public string Alias { get; }
}
