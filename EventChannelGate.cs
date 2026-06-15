using System.Collections.Concurrent;

namespace ClanGuardBot.Services;

/// <summary>
/// One-at-a-time gate per event channel, shared by every component that deletes
/// or re-posts messages in it — currently <see cref="EventChannelSorter"/>
/// (manual <c>/event sort</c>) and <see cref="ClanEventReconciliationService"/>
/// (the self-healing repost sweep). Holding the same lock guarantees a sort and
/// a heal pass can never interleave: without it, the heal sweep could see a
/// message the sorter had just deleted mid-repost as "missing" and post a
/// duplicate, racing the sorter's own MessageId write.
/// </summary>
public sealed class EventChannelGate
{
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Awaits exclusive access for <paramref name="channelId"/>. Dispose the
    /// returned handle (e.g. via <c>using</c>) to release.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(ulong channelId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            // Null-swap so a double-dispose can't release the semaphore twice.
            var g = Interlocked.Exchange(ref _gate, null);
            g?.Release();
        }
    }
}
