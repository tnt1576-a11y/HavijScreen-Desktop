namespace HavijScreen.Native;

// Sustained bandwidth reductions lower pixel count before starving a large
// picture of bits. Recovery needs more headroom and a longer stable period.
internal sealed class ResolutionRate(Quality quality)
{
    readonly int[] levels = quality.Height==0 ? [0] : new[]{480,720,1080,1440}.Where(h=>h<quality.Height).Append(quality.Height).ToArray();
    internal int Height { get; private set; } = quality.Height;
    int candidate=-1;
    long since;
    internal int Observe(int kbps,long now)
    {
        if(quality.Height==0) return 0; // Explicit Source keeps text at native size.
        double budget=Math.Clamp(kbps/(double)quality.Kbps,0,1);
        int wanted=levels[0];
        foreach(int height in levels)
            if(Math.Pow(height/(double)quality.Height,2)<=budget*1.15) wanted=height;
        if(wanted>Height)
        {
            int next=levels.First(h=>h>Height);
            wanted=budget>=Math.Pow(next/(double)quality.Height,2)*.95?next:Height;
        }
        if(wanted==Height) { candidate=-1; return Height; }
        if(candidate!=wanted) { candidate=wanted; since=now; }
        if(now-since >= (wanted<Height?8000:30000)) { Height=wanted; candidate=-1; }
        return Height;
    }
}
