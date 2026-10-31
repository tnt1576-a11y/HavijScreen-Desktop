namespace HavijScreen.Native;

// Access units depend on earlier frames. After skipping one, wait for an IDR
// instead of leaking arbitrary compressed frames into an invalid reference chain.
// Called under the peer's gate; each viewer has its own bounded queue/state.
internal sealed class VideoDelivery
{
    internal const ulong Capacity = 3;
    bool waiting = true;
    internal void Reset() => waiting = true;
    internal bool Accept(bool keyframe, ulong queued)
    {
        if (queued >= Capacity) { waiting = true; return false; }
        if (waiting && !keyframe) return false;
        waiting = false; return true;
    }
}
