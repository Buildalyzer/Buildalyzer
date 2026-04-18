namespace System.Collections;

internal static class EnumerableExtensions
{
    extension(IEnumerable enumerable)
    {
        public bool HasAny => enumerable.GetEnumerator().MoveNext();
    }
}
