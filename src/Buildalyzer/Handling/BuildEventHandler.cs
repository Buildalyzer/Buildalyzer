using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handler for <see cref="BuildEventArgs"/>.</summary>
public interface BuildEventHandler
{
    /// <summary>Handles the <see cref="BuildEventArgs"/>.</summary>
    /// <returns>
    /// An indiciation if the handler could handle the provided args.
    /// </returns>
    bool Handle(BuildEventArgs args, BuildEventHandlerContext context);
}
