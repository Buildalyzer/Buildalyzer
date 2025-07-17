using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Provides a base implementation for <see cref="BuildEventHandler"/>.</summary>
/// <typeparam name="TEvent">
/// The concrete type of the <see cref="BuildEventArgs"/>.
/// </typeparam>
public abstract class BuildEventHandlerBase<TEvent> : BuildEventHandler
    where TEvent : BuildEventArgs
{
    /// <inheritdoc />
    public bool Handle(BuildEventArgs args, BuildEventHandlerContext context)
    {
        if (args is TEvent @event && CanHandle(@event, context))
        {
            Apply(@event, context);
            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>Indicates if event can be handled by the handler.</summary>
    protected virtual bool CanHandle(TEvent e, BuildEventHandlerContext context) => true;

    /// <summary>Applies the event to the context.</summary>
    protected abstract void Apply(TEvent e, BuildEventHandlerContext context);
}
