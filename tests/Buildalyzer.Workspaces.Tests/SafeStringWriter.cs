using System.IO;
using System.Threading;

namespace Buildalyzer.Workspaces.Tests;

// See https://github.com/xunit/xunit/issues/164
internal class SafeStringWriter : StringWriter
{
    private readonly Lock Locker = new();

    public override void Write(char value)
    {
        lock (Locker)
        {
            base.Write(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (Locker)
        {
            base.Write(buffer, index, count);
        }
    }

    public override void Write(string? value)
    {
        lock (Locker)
        {
            base.Write(value);
        }
    }

    public override string ToString()
    {
        lock (Locker)
        {
            return base.ToString();
        }
    }
}
