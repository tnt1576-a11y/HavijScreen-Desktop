using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace HavijScreen.Native;

internal sealed class JoinRequest
{
    internal required string Route, Id, Name, PublicKey;
    internal string Phase = "pending";
    internal DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(10);
    internal byte[]? Key;
    internal ECDiffieHellman? Pair;
    internal NativePeer? Peer;
    internal bool LocalSdpSent, Acknowledged;
    internal long LastResponse = Environment.TickCount64, LastPing;
    internal readonly object Sync = new();
    internal readonly List<(uint Line,string Candidate)> Candidates = [];
}
internal sealed class NativeLobby : IDisposable
{
    readonly NativeRoom room;
    readonly ConcurrentDictionary<string,JoinRequest> requests = new();
    readonly HashSet<string> seen = [];
    readonly Queue<string> nonces = new();
    readonly CancellationTokenSource lifetime = new();
    CodeKeys? keys;
    SetupSocket? socket;
    JoinRequest? guest;
    bool host, disposed;
    internal string State { get; private set; } = "idle";
    internal string Status { get; private set; } = "";
    internal string Code => host ? keys?.Code ?? "" : "";
    internal bool Hosting => host && keys != null;
    internal bool Joining => !host && guest != null && State != "connected";
    internal IReadOnlyList<JoinRequest> Requests => requests.Values.OrderBy(r => r.Expires).ToArray();
    internal event Action? Changed;
    internal event Action<string>? Failure;
    internal NativeLobby(NativeRoom room)
    {
        this.room = room; room.Changed += CheckConnected;
        _ = Pump();
    }
    void SetState(string state, string status = "") { if (disposed) return; State = state; Status = status; Changed?.Invoke(); }
    async Task EnsureSocket(string id)
    {
        if (socket == null)
        {
            socket = new SetupSocket(id); socket.Message += Receive;
            socket.Offline += () =>
            {
                if (disposed) return;
                if (host) SetState("offline","Room code offline. Existing streams continue. Reconnect to accept new people.");
                else if (guest?.Peer?.Established == true) CheckConnected();
                else Fail("The connection service disconnected before joining. Please try again.");
            };
        }
        await socket.Connect(lifetime.Token);
    }
    internal async Task<string> Host(string name, bool stun)
    {
        if (!room.IsHost || guest != null) throw new InvalidOperationException("Only the host can invite people.");
        if (room.Peers.Count != 0 && stun != room.Stun) throw new InvalidOperationException("Keep the same network setting for everyone in the room.");
        room.Name = name; room.Stun = stun; host = true; keys ??= SetupCrypto.Keys(SetupCrypto.NewCode());
        SetState("opening","Connecting to service…"); await EnsureSocket(keys.Route); SetState("hosting"); return keys.Code;
    }
    internal async Task Join(string code, string name)
    {
        if (room.Peers.Count != 0 || guest != null || host) throw new InvalidOperationException("Leave the current room before joining another.");
        room.Name = name; keys = SetupCrypto.Keys(code);
        var pair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        guest = new JoinRequest { Route = SetupCrypto.GuestRoute(), Id = SetupCrypto.Id(), Name = name.Trim(), PublicKey = SetupCrypto.PublicKey(pair), Pair = pair, Expires = DateTimeOffset.UtcNow.AddSeconds(18) };
        SetState("opening","Sending request…"); await EnsureSocket(guest.Route);
        SetState("waiting","Waiting for the host to accept…");
        await Send(keys.Route,new { type = "request", request = guest.Id, name = guest.Name, publicKey = guest.PublicKey, version = 1 });
    }
    Task Send(string destination, object body, byte[]? key = null)
    {
        if (disposed || keys == null || socket == null) return Task.CompletedTask;
        return socket.Send(destination,SetupCrypto.Seal(key ?? keys.Key,socket.Id,destination,body),lifetime.Token);
    }
    async Task Receive(string src, JsonElement envelope)
    {
        if (disposed || socket == null || keys == null || (!host && src != keys.Route) || (host && !src.StartsWith("hsn1-guest-"))) return;
        JoinRequest? record = host ? requests.GetValueOrDefault(src) : guest;
        JsonElement message; bool pairEncrypted = false;
        try { message = SetupCrypto.Open(keys.Key,src,socket.Id,envelope); }
        catch (CryptographicException)
        {
            if (record?.Key == null) return;
            message = SetupCrypto.Open(record.Key,src,socket.Id,envelope); pairEncrypted = true;
        }
        string nonce = message.Text("nonce",22), request = message.Text("request",22);
        if (!SetupCrypto.ValidId(request) || !seen.Add(nonce)) return;
        nonces.Enqueue(nonce); if (nonces.Count > 256) seen.Remove(nonces.Dequeue());
        if (host) await HostMessage(src,message,pairEncrypted); else await GuestMessage(message,pairEncrypted);
    }
    async Task HostMessage(string src, JsonElement message, bool encrypted)
    {
        string type = message.Text("type"), id = message.Text("request",22);
        if (type == "request" && !encrypted)
        {
            string name = message.Text("name",32), publicKey = message.Text("publicKey",87);
            if (message.Int("version") != 1 || !SetupCrypto.ValidName(name) || publicKey.Length != 87 || requests.ContainsKey(src)) return;
            if (room.Peers.Count >= 3 || requests.Count >= 6) { await Send(src,new { type = "declined", request = id, reason = "The room is full or has too many pending requests." }); return; }
            var incoming = new JoinRequest { Route = src, Id = id, Name = name.Trim(), PublicKey = publicKey };
            requests[src] = incoming; Changed?.Invoke(); await Send(src,new { type = "queued", request = id, name = room.Name }); return;
        }
        if (!requests.TryGetValue(src,out var record) || id != record.Id) return;
        if (type == "cancel") { Discard(record); return; }
        if (type == "ping" && !encrypted && record.Phase == "pending") { await Send(src,new { type = "queued", request = id, name = room.Name }); return; }
        if (type == "ready" && encrypted && record.Phase == "approved")
        {
            record.Phase = "preparing"; record.Peer = room.AddPeer(SetupCrypto.Id(),record.Name,true); WireIce(record);
            try
            {
                var offer = await record.Peer.CreateDescription(true,lifetime.Token,false);
                if (disposed || !requests.ContainsKey(src)) return;
                record.Phase = "offer";
                await Send(src,new { type = "offer", request = id, sdp = offer, hostId = room.Id, peerId = record.Peer.Id, roomId = room.RoomId, stun = room.Stun, name = room.Name },record.Key);
                await FlushIce(record); Changed?.Invoke();
            }
            catch { await Reject(src,"Could not prepare the direct connection. Please try again."); }
        }
        else if (type == "answer" && encrypted && record.Phase == "offer" && record.Peer != null)
        {
            record.Phase = "connecting";
            try { await record.Peer.ApplyDescription(NativeRoom.ParseDescription(message.Item("sdp")),false,lifetime.Token); }
            catch { await Reject(src,"The direct connection reply was not valid. Please try again."); }
        }
        else if (type == "candidate" && encrypted && record.Peer != null) record.Peer.AddCandidate((uint)message.Int("line"),message.Text("candidate",4096));
    }
    internal async Task Approve(string route)
    {
        if (!requests.TryGetValue(route,out var record) || record.Phase != "pending") return;
        int reserved = requests.Values.Count(r => r.Phase != "pending" && r.Peer == null);
        if (room.Peers.Count+reserved >= 3) { await Reject(route,"The room is full."); return; }
        record.Phase = "approving"; record.Expires = DateTimeOffset.UtcNow.AddSeconds(90); Changed?.Invoke();
        try
        {
            using var pair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            record.Key = SetupCrypto.PairKey(pair,record.PublicKey,record.Id); record.Phase = "approved";
            await Send(route,new { type = "approved", request = record.Id, publicKey = SetupCrypto.PublicKey(pair) }); Changed?.Invoke();
        }
        catch { await Reject(route,"Could not approve this request. Please try again."); }
    }
    async Task GuestMessage(JsonElement message, bool encrypted)
    {
        var record = guest; if (record == null || message.Text("request",22) != record.Id) return;
        string type = message.Text("type");
        if (type == "declined" && !encrypted)
        {
            // The remote party controls its reason. Display text only, bounded.
            Fail(message.Text("reason",160) is { Length: > 0 } reason ? reason : "The host declined your request.");
        }
        else if (type == "queued" && !encrypted && record.Phase == "pending")
        {
            record.LastResponse = Environment.TickCount64;
            if (!record.Acknowledged) { record.Expires = DateTimeOffset.UtcNow.AddMinutes(10); record.Acknowledged = true; string name = message.Text("name",32); SetState("waiting","Waiting for "+(SetupCrypto.ValidName(name) ? name : "the host")+" to accept…"); }
        }
        else if (type == "approved" && !encrypted && record.Phase == "pending")
        {
            record.Phase = "approved"; record.Expires = DateTimeOffset.UtcNow.AddSeconds(90);
            record.Key = SetupCrypto.PairKey(record.Pair!,message.Text("publicKey",87),record.Id); record.Pair?.Dispose(); record.Pair = null;
            SetState("connecting","Accepted. Connecting directly…"); await Send(keys!.Route,new { type = "ready", request = record.Id },record.Key);
        }
        else if (type == "offer" && encrypted && record.Phase == "approved")
        {
            record.Phase = "answering";
            try
            {
                room.ConfigureGuest(message.Text("peerId",22),message.Text("hostId",22),message.Text("roomId",22),message.Bool("stun"));
                string name = message.Text("name",32); record.Peer = room.AddPeer(room.HostId,SetupCrypto.ValidName(name) ? name : "Host",false); WireIce(record);
                await record.Peer.ApplyDescription(NativeRoom.ParseDescription(message.Item("sdp")),false,lifetime.Token);
                var answer = await record.Peer.CreateDescription(false,lifetime.Token,false);
                if (disposed) return; record.Phase = "connecting";
                await Send(keys!.Route,new { type = "answer", request = record.Id, sdp = answer },record.Key); await FlushIce(record);
            }
            catch (Exception error) { if (!disposed) Fail("Could not prepare the direct connection: "+error.Message); }
        }
        else if (type == "candidate" && encrypted && record.Peer != null) record.Peer.AddCandidate((uint)message.Int("line"),message.Text("candidate",4096));
    }
    void WireIce(JoinRequest record)
    {
        record.Peer!.IceCandidate += (line,candidate) =>
        {
            lock (record.Sync)
            {
                if (disposed || record.Peer.Established || candidate.Length > 4096) return;
                if (!record.LocalSdpSent) { if (record.Candidates.Count < 64) record.Candidates.Add((line,candidate)); return; }
            }
            _ = SendCandidate(record,line,candidate);
        };
    }
    async Task FlushIce(JoinRequest record)
    {
        (uint Line,string Candidate)[] candidates;
        lock(record.Sync) { record.LocalSdpSent = true; candidates = record.Candidates.ToArray(); record.Candidates.Clear(); }
        foreach (var candidate in candidates) await SendCandidate(record,candidate.Line,candidate.Candidate);
    }
    async Task SendCandidate(JoinRequest record, uint line, string candidate)
    {
        if (disposed || record.Peer?.Established == true || record.Key == null) return;
        try { await Send(host ? record.Route : keys!.Route,new { type = "candidate", request = record.Id, line, candidate },record.Key); }
        catch { /* SDP already carries any candidates gathered before it was sent. */ }
    }
    internal async Task Reject(string route, string reason = "The host declined your request.")
    {
        if (!requests.TryGetValue(route,out var record)) return;
        try { await Send(route,new { type = "declined", request = record.Id, reason }); } catch { }
        Discard(record);
    }
    void Discard(JoinRequest record)
    {
        requests.TryRemove(record.Route,out _);
        if (record.Peer != null && !record.Peer.Established) room.Drop(record.Peer.Id);
        record.Pair?.Dispose(); record.Pair = null;
        if (record.Key != null) CryptographicOperations.ZeroMemory(record.Key); record.Key = null;
        Changed?.Invoke();
    }
    void CheckConnected()
    {
        if (disposed) return;
        if (host)
        {
            foreach (var record in requests.Values) if (record.Peer?.Established == true && requests.TryRemove(record.Route,out _)) { var key = Interlocked.Exchange(ref record.Key,null); if (key != null) CryptographicOperations.ZeroMemory(key); }
            Changed?.Invoke();
        }
        else if (guest is { } current && current.Peer?.Established == true && Interlocked.CompareExchange(ref guest,null,current) == current)
        {
            // Multiple native connection/control callbacks can report the same
            // transition. Claim admission before raising events or closing the
            // socket, both of which can reenter this method.
            SetState("connected"); socket?.Dispose();
            if (current.Key != null) CryptographicOperations.ZeroMemory(current.Key); current.Pair?.Dispose();
            var completedKeys = Interlocked.Exchange(ref keys,null); if (completedKeys != null) CryptographicOperations.ZeroMemory(completedKeys.Key);
        }
    }
    async Task Pump()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (host) { foreach (var request in requests.Values) if (request.Expires < DateTimeOffset.UtcNow) await Reject(request.Route,"This join request expired. Please try again."); }
                else if (guest is { } current)
                {
                    if (current.Expires < DateTimeOffset.UtcNow) { Fail(current.Acknowledged ? "Your join request expired. Please try again." : "Room not found, or the host is offline. Check the code and make sure both people use HavijScreen Native."); return; }
                    if (current.Phase == "pending" && current.Acknowledged && Environment.TickCount64-current.LastPing > 10000)
                    {
                        if (Environment.TickCount64-current.LastResponse > 35000) { Fail("The host stopped responding. Check with them and try again."); return; }
                        current.LastPing = Environment.TickCount64; await Send(keys!.Route,new { type = "ping", request = current.Id });
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!host && !disposed) Fail("The connection service went offline before joining."); }
    }
    void Fail(string error) { if (disposed || State == "connected") return; SetState("failed",error); Failure?.Invoke(error); }
    internal async Task Stop()
    {
        if (disposed) return;
        try
        {
            using var timeout = new CancellationTokenSource(500);
            if (guest != null && keys != null && socket?.Open == true) await Send(keys.Route,new { type = "cancel", request = guest.Id }).WaitAsync(timeout.Token);
            if (host) foreach (var request in requests.Values) await Send(request.Route,new { type = "declined", request = request.Id, reason = "The host stopped accepting requests." }).WaitAsync(timeout.Token);
        }
        catch { }
        Dispose();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); room.Changed -= CheckConnected; socket?.Dispose();
        foreach (var request in requests.Values) Discard(request);
        requests.Clear(); guest?.Pair?.Dispose(); if (guest?.Key != null) CryptographicOperations.ZeroMemory(guest.Key);
        if (keys != null) CryptographicOperations.ZeroMemory(keys.Key); guest = null; keys = null;
    }
}
