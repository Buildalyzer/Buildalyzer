using System.IO;

namespace Buildalyzer.Workspaces.Tests;

// See https://github.com/xunit/xunit/issues/164
internal class SafeStringWriter : StringWriter
{
    private readonly object _lock = new();

    public override void Write(char value)
    {
        lock (_lock)
        {
            base.Write(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_lock)
        {
            base.Write(buffer, index, count);
        }
    }

    public override void Write(string value)
    {
        lock (_lock)
        {
            base.Write(value);
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return base.ToString();
        }
    }
}