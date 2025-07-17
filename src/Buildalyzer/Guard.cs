
using System.Runtime.CompilerServices;

namespace Buildalyzer;

/// <summary>Supplies parameter guarding for methods and constructors.</summary>
/// <remarks>
/// Advised usage:
/// * Change the namespace to maximum shared namespace amongst the using projects
/// * Keep it internal and use [assembly: InternalsVisibleTo] to open up access
/// * Add specific Guard methods if you software needs them.
/// * Keep the checks cheap so that you also can run them in production code.
/// </remarks>
[ExcludeFromCodeCoverage]
internal static class Guard
{
    /// <summary>Guards the parameter if not null, otherwise throws an argument (null) exception.</summary>
    /// <typeparam name="T">The type to guard; cannot be a structure.</typeparam>
    /// <param name="parameter">The parameter to guard.</param>
    /// <param name="paramName">The name of the parameter.</param>
    /// <returns>
    /// The guarded parameter.
    /// </returns>
    [DebuggerStepThrough]
    public static T NotNull<T>([ValidatedNotNull] T? parameter, [CallerArgumentExpression(nameof(parameter))] string? paramName = null)
        where T : class
        => parameter ?? throw new ArgumentNullException(paramName);

    /// <summary>Marks the NotNull argument as being validated for not being null, to satisfy the static code analysis.</summary>
    /// <remarks>
    /// Notice that it does not matter what this attribute does, as long as
    /// it is named ValidatedNotNullAttribute.
    ///
    /// It is marked as conditional, as does not add anything to have the attribute compiled.
    /// </remarks>
    [Conditional("Analysis")]
    [AttributeUsage(AttributeTargets.Parameter)]
    private sealed class ValidatedNotNullAttribute : Attribute;
}
