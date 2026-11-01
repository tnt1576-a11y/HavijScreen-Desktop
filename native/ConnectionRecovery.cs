namespace HavijScreen.Native;

internal sealed class ConnectionRecovery
{
    internal const int GraceMs=30000, SilenceMs=8000;
    long lastContact, interrupted;
    bool transport=true, heartbeat;
    internal bool Reconnecting { get; private set; }
    internal void Enable(long now) { heartbeat=true; lastContact=now; }
    internal void Contact(long now) { lastContact=now; transport=true; Reconnecting=false; }
    internal void Transport(bool up,long now)
    {
        transport=up;
        if(!up) Begin(now);
        else if(!heartbeat) Reconnecting=false;
    }
    void Begin(long now) { if(!Reconnecting) { Reconnecting=true; interrupted=now; } }
    internal bool Tick(long now)
    {
        if(heartbeat && now-lastContact>=SilenceMs) Begin(now);
        return Reconnecting && now-interrupted>=GraceMs;
    }
}
