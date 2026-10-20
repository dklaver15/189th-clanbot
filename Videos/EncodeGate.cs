namespace ClanGuardBot.Services;

/// <summary>
/// One video encode at a time on the droplet, shared by <see cref="VideoUpscaler"/>
/// and <see cref="ShortRenderer"/>: two concurrent x264 encodes would blow the
/// 640m container and starve the gateway. A second encode waits its turn.
/// </summary>
public sealed class EncodeGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for the slot; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Release(_gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
