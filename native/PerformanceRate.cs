namespace HavijScreen.Native;

// Uses our encoded-frame cadence, not game FPS or GPU utilization. A healthy
// lower rate only permits a cautious probe; it does not prove spare GPU capacity.
internal sealed class PerformanceRate
{
    static readonly int[] steps = [15,20,30];
    int level=2, slow;
    long lastTime, lastFrames, settleUntil, healthySince, nextProbe;
    int probeDelay=30000;
    internal int Target => steps[level];
    internal void Reset(long now,long frames)
    {
        level=2; slow=0; lastTime=now; lastFrames=frames;
        settleUntil=now+8000; healthySince=0; nextProbe=0; probeDelay=30000;
    }
    internal int Observe(long now,long frames)
    {
        long elapsed=now-lastTime, count=frames-lastFrames;
        lastTime=now; lastFrames=frames;
        // Ignore startup, suspension, counter resets and a source producing no
        // frames (for example a minimized window); none is recovery evidence.
        if(now<settleUntil || elapsed<1500 || elapsed>6000 || count<=0)
        {
            slow=0; healthySince=0; return Target;
        }
        double ratio=count*1000.0/elapsed/Target;
        if(ratio<.85)
        {
            healthySince=0;
            if(++slow>=3 && level>0)
            {
                level--; slow=0; settleUntil=now+4000;
                nextProbe=now+probeDelay; probeDelay=Math.Min(300000,probeDelay*2);
            }
        }
        else
        {
            slow=0;
            if(ratio>=.95)
            {
                if(healthySince==0) healthySince=now;
                if(level<steps.Length-1 && now>=nextProbe && now-healthySince>=30000)
                {
                    level++; healthySince=0; settleUntil=now+4000;
                }
            }
            else healthySince=0;
        }
        return Target;
    }
}
