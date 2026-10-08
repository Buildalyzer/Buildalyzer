using System.IO;
using System.Threading.Tasks;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;

namespace TextWriterLoggerProvider_specs;

public class Is_thread_safe
{
    [Test]
    public void when_many_loggers_write_concurrently_to_a_non_thread_safe_writer()
    {
        // Buildalyzer logs from several threads at once (most notably the process stdout and
        // stderr readers). A plain StringWriter is not thread-safe, so the provider must
        // serialize writes; otherwise concurrent writes corrupt the backing StringBuilder and
        // can throw an ArgumentOutOfRangeException.
        using StringWriter writer = new();
        using TextWriterLoggerProvider provider = new(writer);

        const int threads = 16;
        const int messagesPerThread = 500;
        const string message = "0123456789";

        Parallel.For(0, threads, _ =>
        {
            ILogger logger = provider.CreateLogger("category");
            for (int i = 0; i < messagesPerThread; i++)
            {
                logger.LogInformation(message);
            }
        });

        writer.ToString().Length.Should().Be(threads * messagesPerThread * message.Length);
    }
}
