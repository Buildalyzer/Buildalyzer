
namespace Buildalyzer.Diagnostics;

/// <summary>Allows the debugger to display collections.</summary>
internal sealed class CollectionDebugView<T>
{
    /// <summary>A reference to the enumeration to display.</summary>
    private readonly IEnumerable<T> _enumeration;

    /// <summary>Initializes a new instance of the <see cref="CollectionDebugView{T}"/> class..</summary>
    public CollectionDebugView(IEnumerable<T> enumeration) => _enumeration = enumeration;

    /// <summary>The array that is shown by the debugger.</summary>
    /// <remarks>
    /// Every time the enumeration is shown in the debugger, a new array is created.
    /// By doing this, it is always in sync with the current state of the enumeration.
    /// </remarks>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T[] Items => _enumeration.ToArray();
}
