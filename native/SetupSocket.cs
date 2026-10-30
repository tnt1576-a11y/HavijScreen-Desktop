using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HavijScreen.Native;

internal sealed class SetupSocket : IDisposable
{
    readonly SemaphoreSlim sends = new(1,1);
    readonly string token = SetupCrypto.Id(24);
    ClientWebSocket? socket;
    CancellationTokenSource? connection;
    TaskCompletionSource<bool>? ready;
    int queued;
    bool disposed;
    internal string Id { get; }
    internal bool Open { get; private set; }
    internal event Func<string,JsonElement,Task>? Message;
    internal event Action? Offline;
    internal SetupSocket(string id) { Id = id; }
    internal async Task Connect(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (Open) return;
        connection?.Cancel(); socket?.Dispose();
        connection = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        socket = new ClientWebSocket(); var active = socket;
        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token); timeout.CancelAfter(15000);
        try
        {
            var url = new Uri("wss://0.peerjs.com/peerjs?key=peerjs&id="+Uri.EscapeDataString(Id)+"&token="+Uri.EscapeDataString(token)+"&version=1.5.5");
            await active.ConnectAsync(url,timeout.Token);
            _ = Read(active,connection.Token);
            await ready.Task.WaitAsync(timeout.Token);
            _ = Heartbeat(active,connection.Token);
        }
        catch { active.Abort(); throw new InvalidOperationException("The public connection service is unavailable. Check your internet connection and try again."); }
    }
    async Task Read(ClientWebSocket active, CancellationToken cancellation)
    {
        byte[] buffer = new byte[16384]; int rate = 0; long rateStart = Environment.TickCount64;
        try
        {
            while (!cancellation.IsCancellationRequested && active.State == WebSocketState.Open)
            {
                var bytes = new ArrayBufferWriter<byte>(); ValueWebSocketReceiveResult result;
                do
                {
                    result = await active.ReceiveAsync(buffer.AsMemory(),cancellation);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text || bytes.WrittenCount+result.Count > 190000) throw new InvalidOperationException("Invalid service message.");
                    bytes.Write(buffer.AsSpan(0,result.Count));
                } while (!result.EndOfMessage);
                if (Environment.TickCount64-rateStart > 1000) { rate = 0; rateStart = Environment.TickCount64; }
                if (++rate > 32) continue;
                using var document = JsonDocument.Parse(bytes.WrittenMemory,new JsonDocumentOptions { MaxDepth = 24 });
                var message = document.RootElement; string type = message.Text("type");
                if (type == "OPEN") { Open = true; ready?.TrySetResult(true); }
                else if (type is "ERROR" or "ID-TAKEN") throw new InvalidOperationException("This room code is unavailable.");
                else if (Open && type == "OFFER" && message.Text("dst") == Id && message.Item("payload").Text("label") == "havijscreen-native-v1")
                {
                    string src = message.Text("src");
                    if (!Regex.IsMatch(src,"^hsn1-(room-[a-f0-9]{40}|guest-[a-f0-9]{32})$")) continue;
                    try { if (Message != null) await Message(src,message.Item("payload").Item("metadata").Clone()); }
                    catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ready?.TrySetException(error); }
        finally
        {
            if (ReferenceEquals(active,socket)) { bool wasOpen = Open; Open = false; ready?.TrySetException(new IOException("Connection service disconnected.")); if (!disposed && !cancellation.IsCancellationRequested && wasOpen) Offline?.Invoke(); }
        }
    }
    async Task Heartbeat(ClientWebSocket active, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await timer.WaitForNextTickAsync(cancellation) && ReferenceEquals(active,socket)) await SendBytes("{\"type\":\"HEARTBEAT\"}"u8.ToArray(),cancellation); }
        catch (OperationCanceledException) { }
        catch { active.Abort(); }
    }
    internal Task Send(string destination, SetupEnvelope envelope, CancellationToken cancellation = default) => SendBytes(JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "OFFER", dst = destination,
        payload = new { type = "data", connectionId = "hs-"+SetupCrypto.Id(), label = "havijscreen-native-v1", reliable = true, serialization = "json", sdp = new { type = "offer", sdp = "v=0\r\n" }, metadata = envelope }
    }),cancellation);
    async Task SendBytes(byte[] data, CancellationToken cancellation)
    {
        if (data.Length > 190000) throw new InvalidOperationException("Connection message is too large.");
        if (Interlocked.Increment(ref queued) > 32) { Interlocked.Decrement(ref queued); throw new InvalidOperationException("Connection service is busy."); }
        try
        {
            await sends.WaitAsync(cancellation);
            try
            {
                if (disposed || socket?.State != WebSocketState.Open) throw new InvalidOperationException("Connection service is offline.");
                await socket.SendAsync(data,WebSocketMessageType.Text,true,cancellation);
            }
            finally { sends.Release(); }
        }
        finally { Interlocked.Decrement(ref queued); }
    }
    public void Dispose() { if (disposed) return; disposed = true; Open = false; connection?.Cancel(); socket?.Abort(); socket?.Dispose(); }
}
