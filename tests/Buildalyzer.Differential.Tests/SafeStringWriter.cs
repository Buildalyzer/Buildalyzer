using System.Threading;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// A thread-safe <see cref="StringWriter"/>. Buildalyzer writes to its log writer from
/// several threads while a build is in flight, so an ordinary <see cref="StringWriter"/>
/// (whose backing <see cref="System.Text.StringBuilder"/> is not thread-safe) can corrupt
/// or throw during a concurrent read. See https://github.com/xunit/xunit/issues/164.
/// </summary>
internal sealed class SafeStringWriter : StringWriter
{
    private readonly Lock _locker = new();

    public override void Write(char value)
    {
        lock (_locker)
        {
            base.Write(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_locker)
        {
            base.Write(buffer, index, count);
        }
    }

    public override void Write(string? value)
    {
        lock (_locker)
        {
            base.Write(value);
        }
    }

    public override string ToString()
    {
        lock (_locker)
        {
            return base.ToString();
        }
    }
}
