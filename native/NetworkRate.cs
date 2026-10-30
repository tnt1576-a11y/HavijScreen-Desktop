namespace HavijScreen.Native;

internal sealed record NetworkFeedback(uint Sequence,double Loss,double Rtt);

// Each viewer has an estimate; the shared encoder follows the slowest active
// viewer. Missing/repeated RTCP reports never count as evidence of recovery.
internal sealed class NetworkRate
{
    sealed class Estimate { internal uint Sequence; internal int Kbps,Good; internal double Baseline; internal long Changed; internal bool Seen; }
    readonly Dictionary<string,Estimate> viewers=[];
    readonly int ceiling, floor;
    internal NetworkRate(int ceiling) { this.ceiling=ceiling; floor=Math.Min(ceiling,Math.Max(300,ceiling/8)); }
    internal int Target=>viewers.Count==0?ceiling:viewers.Values.Min(v=>v.Kbps);
    internal void Retain(IEnumerable<string> active) { var ids=active.ToHashSet(); foreach(var id in viewers.Keys.Where(k=>!ids.Contains(k)).ToArray()) viewers.Remove(id); }
    internal void Observe(string id,NetworkFeedback feedback,long now)
    {
        if(!double.IsFinite(feedback.Loss)||!double.IsFinite(feedback.Rtt)||feedback.Loss<0||feedback.Loss>1||feedback.Rtt<0) return;
        if(!viewers.TryGetValue(id,out var v)) viewers[id]=v=new() { Kbps=ceiling };
        if(v.Seen&&v.Sequence==feedback.Sequence) return;
        v.Seen=true; v.Sequence=feedback.Sequence;
        if(feedback.Rtt>0) v.Baseline=v.Baseline==0?feedback.Rtt:Math.Min(v.Baseline,feedback.Rtt);
        bool delay=v.Baseline>0&&feedback.Rtt>Math.Max(.25,v.Baseline*2.5+.08);
        if(feedback.Loss>=.04||delay)
        {
            v.Good=0;
            if(now-v.Changed>=4000) { v.Kbps=Math.Max(floor,(int)(v.Kbps*(feedback.Loss>=.12?.65:.8))); v.Changed=now; }
        }
        else if(feedback.Loss<.01)
        {
            if(++v.Good>=3&&now-v.Changed>=6000) { v.Kbps=Math.Min(ceiling,v.Kbps+Math.Max(100,v.Kbps/20)); v.Good=0; v.Changed=now; }
        }
        else v.Good=0;
    }
}
