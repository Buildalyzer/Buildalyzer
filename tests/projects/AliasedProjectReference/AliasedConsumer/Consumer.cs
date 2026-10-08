extern alias Lib;

namespace AliasedConsumer
{
    public class Consumer
    {
        public Lib::AliasedLibrary.Widget Create() => new Lib::AliasedLibrary.Widget();
    }
}
