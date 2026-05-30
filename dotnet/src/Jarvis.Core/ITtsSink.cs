using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Core;

/// <summary>
/// Sink for synthesized speech. Implementations synthesize text to audio and
/// play it; SpeakAsync blocks until playback completes (or ct fires).
/// The orchestrator calls this on JarvisReplyComplete.
/// </summary>
public interface ITtsSink
{
    Task SpeakAsync(string text, CancellationToken ct);
}
