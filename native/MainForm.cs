using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.ComponentModel;

namespace HavijScreen.Native;

internal sealed class MainForm : NativeForm
{
    NativeRoom room = new();
    NativeLobby lobby;
    readonly string? runtime;
    readonly TextBox displayName = Ui.TextBox("Player");
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Ui.Surface, ForeColor = Ui.Foreground, Font = new Font("Segoe UI",10) };
    readonly Label count = Ui.Label("1 / 4",9,Ui.Muted), state = Ui.Label("Preparing native media…",10,Ui.Muted), sourceLabel = Ui.Label("",9,Ui.Muted), stats = Ui.Label("",9,Ui.Muted);
    readonly FlowLayoutPanel people = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = Padding.Empty };
    readonly Button invite = Ui.Button("Invite someone"), join = Ui.Button("Join a room"), finish = Ui.Button("Finish joining"), leave = Ui.Button("Leave room"), share = Ui.Button("Start sharing",true), hero = Ui.Button("Share your screen",true);
    readonly Panel stage = new() { Dock = DockStyle.Fill, BackColor = Ui.Surface, Padding = new Padding(1) };
    readonly Panel empty = new() { Dock = DockStyle.Fill, BackColor = Ui.Surface };
    readonly TableLayoutPanel videos = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(11,15,11), Visible = false, Padding = new Padding(5) };
    readonly Dictionary<string,VideoCard> cards = [];
    readonly HashSet<string> pausedViews = [];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Image carrot = Ui.Carrot();
    bool ready, resetting, closing, closeReady, hotkey;
    CaptureSource? currentSource;
    long lastBytes, lastTick;
    const int HotkeyId = 1;
    [DllImport("user32")] static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32")] static extern bool UnregisterHotKey(IntPtr handle, int id);
    string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HavijScreen","Native","preferences.json");
    bool stun = true;
    internal MainForm(string? runtime)
    {
        this.runtime = runtime; lobby = new NativeLobby(room);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Absolute,230)); layout.ColumnStyles.Add(new(SizeType.Percent,100)); layout.ColumnStyles.Add(new(SizeType.Absolute,270));
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22,24,20,20) };
        var identity = new Panel { Dock = DockStyle.Top, Height = 130 };
        var roomHeading = Ui.Label("YOUR SPACE",9,Ui.Muted); roomHeading.SetBounds(0,0,130,24); count.SetBounds(142,0,50,24);
        var nameLabel = Ui.Label("YOUR NAME",8,Ui.Muted); nameLabel.SetBounds(0,46,180,20); displayName.SetBounds(0,72,185,32); displayName.MaxLength = 32; displayName.AccessibleName = "Your name";
        identity.Controls.AddRange([roomHeading,count,nameLabel,displayName]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var button in new[] { invite,join,finish,leave }) { button.Width = 185; actions.Controls.Add(button); }
        left.Controls.Add(people); left.Controls.Add(actions); left.Controls.Add(identity);
        var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22,24,22,20) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 88 }; state.Dock = DockStyle.Top; state.Height = 42; stats.Dock = DockStyle.Bottom; stats.Height = 42; bottom.Controls.Add(stats); bottom.Controls.Add(state);
        stage.Paint += (_,e) => { using var pen = new Pen(Ui.Line); e.Graphics.DrawRectangle(pen,0,0,stage.Width-1,stage.Height-1); };
        empty.Paint += (_,e) => e.Graphics.DrawImage(carrot,new Rectangle((empty.Width-90)/2,(empty.Height-90)/2-52,90,90));
        empty.Resize += (_,_) => { hero.SetBounds((empty.Width-190)/2,empty.Height/2+38,190,44); empty.Invalidate(); };
        hero.Width = 190; empty.Controls.Add(hero); stage.Controls.Add(empty); stage.Controls.Add(videos); center.Controls.Add(stage); center.Controls.Add(bottom);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22,24,22,20), BackColor = Color.FromArgb(20,25,20) };
        var streamHeading = Ui.Label("YOUR STREAM",9,Ui.Muted); streamHeading.SetBounds(22,24,220,25);
        var qualityLabel = Ui.Label("STREAM QUALITY",8,Ui.Muted); qualityLabel.SetBounds(22,82,220,24);
        quality.SetBounds(22,114,226,36); quality.AccessibleName = "Stream quality"; quality.Items.AddRange(Quality.All); quality.SelectedIndex = 0;
        sourceLabel.SetBounds(22,178,224,90); sourceLabel.AutoEllipsis = true;
        var actionBottom = new Panel { Dock = DockStyle.Bottom, Height = 105 }; share.Dock = DockStyle.Top; share.Height = 44;
        var shortcut = Ui.Label("Stop · Ctrl + Shift + S",8,Ui.Muted); shortcut.Dock = DockStyle.Bottom; shortcut.TextAlign = ContentAlignment.MiddleCenter; actionBottom.Controls.Add(share); actionBottom.Controls.Add(shortcut);
        right.Controls.AddRange([streamHeading,qualityLabel,quality,sourceLabel,actionBottom]); layout.Controls.Add(left,0,0); layout.Controls.Add(center,1,0); layout.Controls.Add(right,2,0); Content.Controls.Add(layout);
        ReadSettings(); Bind();
        invite.Click += (_,_) => OpenRoomDialog(false); join.Click += (_,_) => OpenRoomDialog(true); finish.Click += (_,_) => OpenRoomDialog(true);
        leave.Click += async (_,_) => await Reset(); share.Click += async (_,_) => await ToggleShare(); hero.Click += async (_,_) => await ToggleShare();
        quality.SelectedIndexChanged += async (_,_) =>
        {
            SaveSettings(); if (room.Sharing && currentSource != null) try { await room.Share(currentSource,(Quality)quality.SelectedItem!,sourceLabel.Tag is true); } catch(Exception error) { ShowNotice(error.Message); }
        };
        displayName.Leave += (_,_) => SaveSettings(); timer.Tick += (_,_) => UpdateStats();
        Resize += (_,_) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                foreach(var item in cards.Where(item => item.Value.Active).ToArray()) { pausedViews.Add(item.Key); room.Watch(item.Key,IntPtr.Zero,false); }
            }
            else if (pausedViews.Count > 0)
            {
                foreach(string id in pausedViews.ToArray()) { var peer = room.Connected.FirstOrDefault(p => p.Id == id && p.Sharing); if (peer != null) Watch(peer); }
                pausedViews.Clear();
            }
        };
        people.Resize += (_,_) => { foreach(Control card in people.Controls) card.Width = Math.Max(155,people.ClientSize.Width-16); };
        Shown += async (_,_) =>
        {
            hotkey = RegisterHotKey(Handle,HotkeyId,0x0002|0x0004|0x4000,(uint)Keys.S);
            if (!hotkey) shortcut.Text = "Stop in HavijScreen · Ctrl + Shift + S";
            try { await Task.Run(() => { Gst.Initialize(runtime); _ = MediaCapture.SelectEncoder(); }); ready = true; state.Text = "Not sharing"; }
            catch(Exception error) { state.Text = error.Message; }
            Render(); timer.Start();
        };
        FormClosing += async (_,e) =>
        {
            if (closeReady) return;
            e.Cancel = true; if (closing) return; closing = true; Enabled = false;
            try { await lobby.Stop(); room.Dispose(); await room.Completion.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
            closeReady = true; Close();
        };
        FormClosed += (_,_) => { timer.Stop(); if (hotkey) UnregisterHotKey(Handle,HotkeyId); lobby.Dispose(); room.Dispose(); carrot.Dispose(); };
        KeyPreview = true; KeyDown += async (_,e) => { if (e.Control && e.Shift && e.KeyCode == Keys.S && room.Sharing) { e.Handled = true; await StopSharing(); } };
        Render();
    }
    void Bind(bool roomEvents = true)
    {
        var boundRoom = room; var boundLobby = lobby;
        if (roomEvents)
        {
            room.Changed += () => Ui.On(this,() => { if (room == boundRoom) Render(); });
            room.Notice += message => Ui.On(this,() => { if (room == boundRoom) ShowNotice(message); });
            room.Unwatch += id => Ui.On(this,() => { if (room == boundRoom && cards.TryGetValue(id,out var card)) { card.Active = false; ArrangeVideos(); } });
            room.Ended += message => Ui.On(this,async () => { if (room == boundRoom) { await Reset(); ShowNotice(message); } });
        }
        lobby.Changed += () => Ui.On(this,() => { if (lobby == boundLobby) Render(); });
        lobby.Failure += message => Ui.On(this,async () => { if (lobby == boundLobby) { await Reset(); ShowNotice(message); } });
    }
    void Render()
    {
        if (IsDisposed) return;
        count.Text = (room.Connected.Count+1)+" / 4";
        invite.Enabled = ready && room.IsHost && !lobby.Joining && room.Peers.Count < 3;
        join.Visible = room.Peers.Count == 0 && !lobby.Hosting && !lobby.Joining;
        join.Enabled = ready; finish.Visible = lobby.Joining; leave.Visible = room.Peers.Count > 0 || lobby.Hosting || lobby.Joining;
        displayName.Enabled = room.Peers.Count == 0 && !lobby.Hosting && !lobby.Joining;
        share.Enabled = hero.Enabled = ready && !resetting; share.Text = room.Sharing ? "Stop sharing" : "Start sharing"; hero.Visible = !room.Sharing;
        people.SuspendLayout(); foreach(Control old in people.Controls.Cast<Control>().ToArray()) old.Dispose(); people.Controls.Clear();
        AddPerson(displayName.Text,room.IsHost ? "You · Room host" : "You",null);
        foreach(var peer in room.Connected) AddPerson(peer.Name,peer.Sharing ? "Screen available" : (peer.Id == room.HostId ? "Room host" : "Connected"),peer);
        foreach(var request in lobby.Requests)
        {
            var card = Person(request.Name,request.Phase == "pending" ? "Wants to join" : "Connecting…",105);
            var accept = Ui.Button("Accept",true); accept.SetBounds(10,58,74,30); accept.Visible = request.Phase == "pending";
            var decline = Ui.Button(request.Phase == "pending" ? "Decline" : "Cancel"); decline.SetBounds(91,58,74,30);
            accept.Click += async (_,_) => { try { await lobby.Approve(request.Route); } catch(Exception error) { ShowNotice(error.Message); } };
            decline.Click += async (_,_) => await lobby.Reject(request.Route); card.Controls.AddRange([accept,decline]); people.Controls.Add(card);
        }
        people.ResumeLayout();
        if (lobby.State == "offline") state.Text = lobby.Status;
        else if (lobby.Joining) state.Text = lobby.Status;
        else state.Text = room.Sharing ? $"Sharing · {room.Viewers} watching · {(sourceLabel.Tag is true ? (currentSource?.Monitor == true ? "System audio" : "App audio") : "Video only")}" : (ready ? "Not sharing" : state.Text);
        ArrangeVideos(); UpdateStats();
    }
    Panel Person(string name, string detail, int height)
    {
        var card = new Panel { Width = Math.Max(155,people.ClientSize.Width-16), Height = height, BackColor = Ui.Surface, Margin = new Padding(0,0,0,10) };
        var label = Ui.Label(name,10); label.Font = new Font("Segoe UI",10,FontStyle.Bold); label.SetBounds(10,6,card.Width-20,24); label.AutoEllipsis = true; label.Anchor = AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        var sub = Ui.Label(detail,8,Ui.Muted); sub.SetBounds(10,30,card.Width-20,22); sub.Anchor = AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        card.Controls.AddRange([label,sub]); return card;
    }
    void AddPerson(string name, string detail, NativePeer? peer)
    {
        var card = Person(name,detail,peer != null && (peer.Sharing || room.IsHost) ? 100 : 62);
        if (peer != null)
        {
            if (peer.Sharing)
            {
                var watch = Ui.Button(peer.Subscribed ? "Stop watching" : "Watch stream"); watch.SetBounds(10,58,110,30);
                watch.Click += (_,_) => { if (peer.Subscribed) room.Watch(peer.Id,IntPtr.Zero,false); else Watch(peer); }; card.Controls.Add(watch);
            }
            if (room.IsHost)
            {
                var remove = Ui.Button("×"); remove.AccessibleName = "Remove "+name; remove.SetBounds(128,58,36,30); remove.Click += (_,_) => room.Remove(peer.Id); card.Controls.Add(remove);
            }
        }
        people.Controls.Add(card);
    }
    void Watch(NativePeer peer)
    {
        if (!cards.TryGetValue(peer.Id,out var card))
        {
            card = new VideoCard(peer); cards[peer.Id] = card; videos.Controls.Add(card);
            card.Stop += () => room.Watch(peer.Id,IntPtr.Zero,false);
        }
        card.Active = true; ArrangeVideos(); room.Watch(peer.Id,card.VideoHandle,true,card.Sound);
    }
    void ArrangeVideos()
    {
        var visible = cards.Values.Where(c => c.Active).ToArray(); videos.Visible = visible.Length > 0; empty.Visible = visible.Length == 0;
        foreach(var card in cards.Values) card.Visible = card.Active;
        if (visible.Length == 0) return;
        videos.SuspendLayout(); videos.ColumnStyles.Clear(); videos.RowStyles.Clear(); videos.ColumnCount = visible.Length == 1 ? 1 : 2; videos.RowCount = visible.Length > 2 ? 2 : 1;
        for(int i = 0; i < videos.ColumnCount; i++) videos.ColumnStyles.Add(new(SizeType.Percent,100f/videos.ColumnCount));
        for(int i = 0; i < videos.RowCount; i++) videos.RowStyles.Add(new(SizeType.Percent,100f/videos.RowCount));
        for(int i = 0; i < visible.Length; i++) { videos.SetCellPosition(visible[i],new TableLayoutPanelCellPosition(i%videos.ColumnCount,i/videos.ColumnCount)); visible[i].Dock = DockStyle.Fill; }
        videos.ResumeLayout(); if (videos.Visible) videos.BringToFront();
    }
    async Task ToggleShare()
    {
        if (!ready || resetting) return;
        if (room.Sharing) { await StopSharing(); return; }
        using var picker = new SourceDialog(); if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
        try
        {
            currentSource = picker.Selected; sourceLabel.Tag = picker.Audio;
            sourceLabel.Text = currentSource.Name+"\n\n"+(picker.Audio ? (currentSource.Monitor ? "All system audio" : "Selected app audio") : "Video only");
            await room.Share(currentSource,(Quality)quality.SelectedItem!,picker.Audio); Render();
        }
        catch(Exception error) { ShowNotice(error.Message); }
    }
    async Task StopSharing() { try { await room.StopSharing(); Render(); } catch(Exception error) { ShowNotice(error.Message); } }
    void OpenRoomDialog(bool joining)
    {
        if (!ready) return;
        try { room.Name = displayName.Text; } catch(Exception error) { ShowNotice(error.Message); return; }
        using var dialog = new RoomDialog(lobby,joining,displayName.Text,room.Stun,async () =>
        {
            if (joining) await Reset(); else { await lobby.Stop(); lobby = new NativeLobby(room); Bind(false); Render(); }
        });
        dialog.ShowDialog(this); stun = room.Stun; SaveSettings(); Render();
    }
    async Task Reset()
    {
        if (resetting) return; resetting = true; Render();
        var previous = room;
        try { await lobby.Stop(); previous.Dispose(); await previous.Completion.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
        foreach(var card in cards.Values) card.Dispose(); cards.Clear(); pausedViews.Clear();
        room = new NativeRoom { Stun = stun }; lobby = new NativeLobby(room); Bind(); currentSource = null; sourceLabel.Text = ""; lastBytes = 0; resetting = false; Render();
    }
    void UpdateStats()
    {
        long total = room.Peers.Sum(p => p.SentBytes), tick = Environment.TickCount64;
        double mbps = lastTick != 0 && total >= lastBytes && tick > lastTick ? (total-lastBytes)*8.0/(tick-lastTick)/1000 : 0;
        if (tick-lastTick >= 500) { lastBytes = total; lastTick = tick; }
        string encoder = room.Encoder switch { "nvd3d11h264enc" => "NVIDIA NVENC", "amfh264enc" => "AMD AMF", "qsvh264enc" => "Intel Quick Sync", _ => room.Sharing ? "Waiting for a viewer" : "—" };
        stats.Text = $"UPLOAD  {mbps:F2} Mbps     ENCODER  {encoder}     WATCHING YOU  {room.Viewers}";
    }
    void ShowNotice(string message) { state.Text = message; state.ForeColor = Color.FromArgb(234,171,141); }
    void ReadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath) || new FileInfo(SettingsPath).Length > 4096) return;
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var value = json.RootElement;
            if (SetupCrypto.ValidName(value.Text("name",32))) displayName.Text = value.Text("name",32);
            quality.SelectedIndex = Math.Clamp(value.Int("quality"),0,Quality.All.Length-1); stun = value.Bool("stun"); room.Stun = stun;
        }
        catch { }
    }
    void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath,JsonSerializer.Serialize(new { name = displayName.Text, quality = quality.SelectedIndex, stun })); } catch { }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312 && message.WParam.ToInt32() == HotkeyId && room.Sharing) _ = StopSharing();
        base.WndProc(ref message);
    }
}
internal sealed class VideoCard : Panel
{
    readonly NativePeer peer;
    readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Button sound = Ui.Button("Enable sound");
    internal IntPtr VideoHandle => video.Handle;
    internal bool Sound { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Active { get; set; }
    internal event Action? Stop;
    internal VideoCard(NativePeer peer)
    {
        this.peer = peer; BackColor = Color.Black; Margin = new Padding(4); MinimumSize = new Size(200,180);
        var caption = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Ui.Background };
        var name = Ui.Label(peer.Name,9); name.Dock = DockStyle.Left; name.Width = 110;
        var stop = Ui.Button("Stop watching"); stop.Dock = DockStyle.Right; stop.Width = 108; sound.Dock = DockStyle.Right; sound.Width = 108;
        caption.Controls.Add(name); caption.Controls.Add(sound); caption.Controls.Add(stop); Controls.Add(video); Controls.Add(caption);
        stop.Click += (_,_) => Stop?.Invoke(); sound.Click += (_,_) => { Sound = !Sound; sound.Text = Sound ? "Mute sound" : "Enable sound"; peer.SetView(VideoHandle,Sound); };
    }
}
