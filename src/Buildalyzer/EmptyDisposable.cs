#pragma warning disable

namespace Buildalyzer;

[Obsolete("Will be dropped in the next major version.")]
public class EmptyDisposable : IDisposable
{
    public void Dispose()
    {
    }
}
