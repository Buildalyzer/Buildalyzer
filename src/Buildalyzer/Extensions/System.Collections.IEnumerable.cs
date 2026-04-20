namespace System.Collections;

internal static class EnumerableExtensions
{
    extension(IEnumerable enumerable)
    {
        public bool HasAny
        {
            get
            {
                var enumerator = enumerable.GetEnumerator();
                try
                {
                    return enumerator.MoveNext();
                }
                finally
                {
                    if (enumerator is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }
    }
}
