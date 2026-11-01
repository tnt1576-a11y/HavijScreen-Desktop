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
    readonly TextField displayName = Ui.TextBox("Player");
    readonly QualityPicker quality = new();
    readonly Label count = Ui.Label("1 / 4",7.5f,Ui.Muted), state = Ui.Label("Preparing…",8,Ui.Muted), sourceLabel = Ui.Label("",8,Ui.Muted);
    readonly StatsStrip stats = new();
    readonly Panel left = new(), center = new(), right = new(), actions = new(), footer = new();
    readonly CaptionLabel roomHeading = Ui.Caption("YOUR SPACE"), nameHeading = Ui.Caption("YOUR NAME"), peopleHeading = Ui.Caption("IN THE ROOM"), streamHeading = Ui.Caption("YOUR STREAM"), qualityHeading = Ui.Caption("STREAM QUALITY"), sourceHeading = Ui.Caption("SHARING");
    readonly Label qualityHint = Ui.Label("",7,Color.FromArgb(169,184,153)), shortcut = Ui.Label("Stop · Ctrl + Shift + S",7,Ui.Muted);
    readonly ScreenIllustration illustration = new();
    readonly SharingBanner banner = new();
    readonly LocalPreviewToggle preview=new();
    readonly Panel localCard=new() { BackColor=Color.Black,Margin=new Padding(4),Visible=false,AccessibleName="Local preview" }, localVideo=new() { Dock=DockStyle.Fill,BackColor=Color.Black };
    string streamMode="Performance";
    bool captureCursor=true, updatingQuality;
    bool layoutReady;
    readonly FlowLayoutPanel people = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = Padding.Empty };
    readonly StyledButton invite = Ui.Button("Invite someone"), join = Ui.Button("Join a room"), finish = Ui.Button("Finish joining"), leave = Ui.Button("Leave room"), share = Ui.Button("Start sharing",true), hero = Ui.Button("Share your screen",true);
    readonly TheaterPanel stage = new();
    readonly Panel empty = new() { BackColor = Color.Transparent };
    readonly TableLayoutPanel videos = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(11,15,11), Visible = false, Padding = new Padding(5) };
    readonly Dictionary<string,VideoCard> cards = [];
    readonly HashSet<string> pausedViews = [];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };

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
        BuildLayout();
        ReadSettings(); Bind();
        invite.Click += (_,_) => OpenRoomDialog(false); join.Click += (_,_) => OpenRoomDialog(true); finish.Click += (_,_) => OpenRoomDialog(true);
        leave.Click += async (_,_) => await Reset(); share.Click += async (_,_) => await ToggleShare(); hero.Click += async (_,_) => await ToggleShare();
        quality.SelectedIndexChanged += async (_,_) =>
        {
            if(updatingQuality) return;
            streamMode=quality.SelectedItem?.PrioritizeGame==true?"Performance":"Custom";
            SaveSettings(); if (room.Sharing && currentSource != null) try { await room.Share(currentSource,(Quality)quality.SelectedItem!,sourceLabel.Tag is true); } catch(Exception error) { ShowNotice(error.Message); }
        };
        preview.CheckedChanged+=async(_,_)=>{ Render(); await UpdateLocalPreview(); };
        displayName.Leave += (_,_) => SaveSettings(); timer.Tick += (_,_) => UpdateStats();
        Resize += (_,_) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                foreach(var item in cards.Where(item => item.Value.Active && !item.Value.IsDetached).ToArray()) { pausedViews.Add(item.Key); room.Watch(item.Key,IntPtr.Zero,false); }
            }
            else if (pausedViews.Count > 0)
            {
                foreach(string id in pausedViews.ToArray()) { var peer = room.Connected.FirstOrDefault(p => p.Id == id && p.Sharing); if (peer != null) Watch(peer); }
                pausedViews.Clear();
            }
            _=UpdateLocalPreview();
        };
        Shown += async (_,_) =>
        {
            hotkey = RegisterHotKey(Handle,HotkeyId,0x0002|0x0004|0x4000,(uint)Keys.S);
            if (!hotkey) shortcut.Text = "Stop in app · Ctrl + Shift + S";
            try { await Task.Run(() => Gst.Initialize(runtime)); ready = true; state.Text = ""; }
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
        FormClosed += (_,_) => { timer.Stop(); if (hotkey) UnregisterHotKey(Handle,HotkeyId); lobby.Dispose(); room.Dispose(); };
        KeyPreview = true; KeyDown += async (_,e) => { if (e.Control && e.Shift && e.KeyCode == Keys.S && room.Sharing) { e.Handled = true; await StopSharing(); } };
        Render();
    }
    void BuildLayout()
    {
        left.BackColor=center.BackColor=actions.BackColor=footer.BackColor=Ui.Background; right.BackColor=Color.FromArgb(20,23,20);
        left.Paint+=(_,e)=>{ using var pen=new Pen(Ui.Divider); e.Graphics.DrawLine(pen,left.Width-1,0,left.Width-1,left.Height); using var dot=new SolidBrush(Color.FromArgb(144,162,130)); int x=Ui.Px(this,178),y=Ui.Px(this,186),d=Ui.Px(this,4); e.Graphics.FillEllipse(dot,x,y,d,d); };
        right.Paint+=(_,e)=>{ using var pen=new Pen(Ui.Divider); e.Graphics.DrawLine(pen,0,0,0,right.Height); e.Graphics.DrawLine(pen,Ui.Px(this,24),Ui.Px(this,200),right.Width-Ui.Px(this,24),Ui.Px(this,200)); Art.Icon(e.Graphics,Glyph.Screen,new(right.Width-Ui.Px(this,40),Ui.Px(this,30),Ui.Px(this,16),Ui.Px(this,16)),Ui.Muted); Art.Icon(e.Graphics,Glyph.Bolt,new(Ui.Px(this,24),Ui.Px(this,163),Ui.Px(this,12),Ui.Px(this,12)),Ui.Muted); };
        footer.Paint+=(_,e)=>{ Art.Icon(e.Graphics,Glyph.Shield,new(0,Ui.Px(this,5),Ui.Px(this,12),Ui.Px(this,12)),Ui.Muted); using var f=Ui.Font(9); TextRenderer.DrawText(e.Graphics,"Encrypted peer-to-peer media",f,new Rectangle(Ui.Px(this,18),0,Ui.Px(this,190),Ui.Px(this,24)),Ui.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding); };
        displayName.MaxLength=32; displayName.AccessibleName="Your name";
        invite.Kind=ButtonKind.Invite; invite.Symbol=Glyph.Plus; invite.TrailingArrow=true;
        join.Kind=finish.Kind=leave.Kind=ButtonKind.Text; join.Symbol=Glyph.Link; join.Text="Join a friend's room"; join.ForeColor=finish.ForeColor=Ui.Muted; leave.ForeColor=Color.FromArgb(227,162,139);
        share.Symbol=hero.Symbol=Glyph.Screen; share.TrailingArrow=hero.TrailingArrow=true;
        quality.Items.AddRange(Quality.All); quality.SelectedIndex=Array.IndexOf(Quality.All,Quality.Performance); quality.AccessibleName="Stream quality";
        qualityHint.Text="Lowers capture rate if it falls behind."; qualityHint.AutoEllipsis=true;
        quality.SelectedIndexChanged+=(_,_)=>{ qualityHint.Text=quality.SelectedItem?.PrioritizeGame==true?"Lowers capture rate if it falls behind.":quality.SelectedIndex switch { 1=>"More detail, with a moderate frame rate.",2=>"Smoother motion for fast-paced games.",3=>"Extra detail. More GPU and upload use.",_=>"A lighter load. More room for your game." }; };
        sourceLabel.AutoEllipsis=true; sourceLabel.TextAlign=ContentAlignment.TopLeft;
        shortcut.TextAlign=ContentAlignment.MiddleCenter;
        actions.Controls.AddRange([invite,join,finish,leave]); left.Controls.AddRange([roomHeading,count,nameHeading,displayName,peopleHeading,people,actions]);
        empty.Controls.AddRange([illustration,hero]); stage.Controls.AddRange([empty,videos,banner]); banner.BringToFront(); banner.Visible=false; banner.Stop+=async()=>await StopSharing();
        stage.Resize+=(_,_)=>LayoutStage(); footer.Controls.AddRange([state,stats]); center.Controls.AddRange([stage,footer]);
        var localCaption=Ui.Label("Your screen · muted",8,Ui.Muted); localCaption.Dock=DockStyle.Bottom; localCaption.Height=32; localCaption.Padding=new Padding(10,0,0,0); localCard.Controls.AddRange([localVideo,localCaption]); videos.Controls.Add(localCard);
        right.Controls.AddRange([streamHeading,qualityHeading,quality,qualityHint,preview,sourceHeading,sourceLabel,share,shortcut]); Content.Controls.AddRange([left,center,right]);
        layoutReady=true; LayoutUi();
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if(layoutReady) LayoutUi(); }
    void LayoutUi()
    {
        int P(int x)=>Ui.Px(this,x); int width=Content.ClientSize.Width,height=Content.ClientSize.Height,l=P(224),r=P(264),w=Math.Max(1,width-l-r);
        left.SetBounds(0,0,l,height); center.SetBounds(l,0,w,height); right.SetBounds(width-r,0,r,height);
        roomHeading.SetBounds(P(24),P(26),P(130),P(24)); count.SetBounds(l-P(67),P(26),P(43),P(24)); count.TextAlign=ContentAlignment.MiddleRight;
        nameHeading.SetBounds(P(24),P(77),l-P(48),P(20)); displayName.SetBounds(P(24),P(104),l-P(48),P(40)); peopleHeading.SetBounds(P(24),P(175),l-P(48),P(22));
        LayoutSidebar(); stage.SetBounds(P(28),P(28),Math.Max(1,w-P(56)),Math.Max(P(250),height-P(146))); footer.SetBounds(P(28),height-P(106),Math.Max(1,w-P(56)),P(84));
        stats.SetBounds(0,P(25),footer.Width,P(59)); state.SetBounds(P(204),0,Math.Max(1,footer.Width-P(204)),P(24)); state.AutoEllipsis=true;
        streamHeading.SetBounds(P(24),P(26),r-P(72),P(24)); qualityHeading.SetBounds(P(24),P(78),r-P(48),P(22)); quality.SetBounds(P(24),P(109),r-P(48),P(40)); qualityHint.SetBounds(P(42),P(156),r-P(62),P(30));
        preview.SetBounds(P(24),P(211),r-P(48),P(64));
        sourceHeading.SetBounds(P(24),P(305),r-P(48),P(22)); sourceLabel.SetBounds(P(24),P(337),r-P(48),P(88));
        share.SetBounds(P(24),height-P(101),r-P(48),P(44)); shortcut.SetBounds(P(18),height-P(48),r-P(36),P(22));
        LayoutStage();
    }
    void LayoutSidebar()
    {
        if(!layoutReady) return; int P(int x)=>Ui.Px(this,x); int width=left.ClientSize.Width-P(48),top=P(206);
        int desired=people.Controls.Cast<Control>().Sum(c=>c.Height+c.Margin.Vertical);
        int buttons=new[]{invite,join,finish,leave}.Count(b=>b.Visible);
        int available=Math.Max(P(54),left.Height-top-buttons*P(46)-P(30));
        people.SetBounds(P(24),top,width,Math.Min(desired,available));
        foreach(Control card in people.Controls) card.Width=Math.Max(1,width-(desired>available?SystemInformation.VerticalScrollBarWidth:P(2)));
        actions.SetBounds(P(24),top+people.Height+P(10),width,buttons*P(46)); int y=0;
        foreach(var button in new[]{invite,join,finish,leave}) if(button.Visible) { button.SetBounds(0,y,width,P(button==invite?42:36)); y+=P(46); }
    }
    void LayoutStage()
    {
        int P(int x)=>Ui.Px(this,x); empty.SetBounds(P(1),P(1),Math.Max(1,stage.Width-P(2)),Math.Max(1,stage.Height-P(2)));
        int middle=empty.Height/2; illustration.SetBounds((empty.Width-P(180))/2,middle-P(119),P(180),P(160)); hero.SetBounds((empty.Width-P(190))/2,middle+P(49),P(190),P(44));
        banner.SetBounds(P(13),stage.Height-P(59),Math.Max(1,stage.Width-P(26)),P(46)); banner.BringToFront();
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
        AddPerson(displayName.Text,room.IsHost ? "Room host" : "Connected",null);
        foreach(var peer in room.Connected) AddPerson(peer.Name,peer.Reconnecting ? "Reconnecting..." : peer.Sharing ? "Screen available" : (peer.Id == room.HostId ? "Room host" : "Connected"),peer);
        foreach(var request in lobby.Requests)
        {
            var card = Person(request.Name,request.Phase == "pending" ? "Wants to join" : "Connecting…",105);
            var accept = Ui.Button("Accept",true); accept.SetBounds(10,58,74,30); accept.Visible = request.Phase == "pending";
            var decline = Ui.Button(request.Phase == "pending" ? "Decline" : "Cancel"); decline.SetBounds(91,58,74,30);
            card.Resize+=(_,_)=>{ int p=Ui.Px(card,10),gap=Ui.Px(card,8),w=(card.Width-p*2-gap)/2; accept.SetBounds(p,Ui.Px(card,58),w,Ui.Px(card,30)); decline.SetBounds(p+w+gap,Ui.Px(card,58),w,Ui.Px(card,30)); };
            accept.Click += async (_,_) => { try { await lobby.Approve(request.Route); } catch(Exception error) { ShowNotice(error.Message); } };
            decline.Click += async (_,_) => await lobby.Reject(request.Route); card.Controls.AddRange([accept,decline]); people.Controls.Add(card);
        }
        people.ResumeLayout(); LayoutSidebar();
        if (room.CaptureError is { } captureError) { state.Text=captureError; state.ForeColor=Color.FromArgb(234,171,141); }
        else if (lobby.State == "offline") state.Text = lobby.Status;
        else if (lobby.Joining) state.Text = lobby.Status;
        else if(ready) { state.Text=""; state.ForeColor=Ui.Muted; }
        banner.Visible = room.Sharing; banner.SetStatus($"Sharing · {room.Viewers} watching     {(sourceLabel.Tag is true ? (currentSource?.Monitor == true ? "System audio" : "App audio") : "Video only")}");
        sourceLabel.Visible=sourceHeading.Visible=room.Sharing;
        share.Kind=room.Sharing?ButtonKind.Danger:ButtonKind.Primary; share.Symbol=room.Sharing?Glyph.Stop:Glyph.Screen; share.Invalidate();
        ArrangeVideos(); LayoutSidebar(); UpdateStats();
    }
    Panel Person(string name, string detail, int height, bool self=false)
    {
        return new PersonPanel(name,detail,self:self,request:detail is "Wants to join" or "Connecting…") { Width=Math.Max(Ui.Px(this,150),people.ClientSize.Width-Ui.Px(this,4)), Height=Ui.Px(this,height) };
    }
    void AddPerson(string name, string detail, NativePeer? peer)
    {
        var card = Person(name,detail,peer != null && (peer.Sharing || room.IsHost) ? 90 : 54,peer==null);
        if (peer != null)
        {
            if (peer.Sharing)
            {
                var watch = Ui.Button(peer.Subscribed ? "Stop watching" : "Watch stream"); watch.SetBounds(10,58,110,30);
                watch.Symbol=peer.Subscribed?Glyph.Stop:Glyph.Screen;
                card.Resize+=(_,_)=>watch.SetBounds(0,Ui.Px(card,53),Math.Max(1,card.Width-Ui.Px(card,42)),Ui.Px(card,30));
                watch.Click += (_,_) => { if (peer.Subscribed) StopWatching(peer.Id); else Watch(peer); }; card.Controls.Add(watch);
            }
            if (room.IsHost)
            {
                var remove = Ui.Button("×"); remove.AccessibleName = "Remove "+name; remove.SetBounds(128,58,36,30); remove.Click += (_,_) => room.Remove(peer.Id); card.Controls.Add(remove);
                remove.Text=""; remove.Symbol=Glyph.Close; remove.Kind=ButtonKind.Text; remove.ForeColor=Ui.Muted; remove.AccessibleName="Remove "+name;
                card.Resize+=(_,_)=>remove.SetBounds(card.Width-Ui.Px(card,32),Ui.Px(card,53),Ui.Px(card,32),Ui.Px(card,30));
            }
        }
        people.Controls.Add(card);
    }
    void StopWatching(string id)
    {
        pausedViews.Remove(id);
        if(cards.TryGetValue(id,out var card)) card.Active=false;
        room.Watch(id,IntPtr.Zero,false);
        ArrangeVideos();
    }
    void Watch(NativePeer peer)
    {
        if(cards.TryGetValue(peer.Id,out var previous) && !previous.IsBoundTo(peer))
        {
            pausedViews.Remove(peer.Id);
            cards.Remove(peer.Id); videos.Controls.Remove(previous); previous.Dispose();
        }
        if (!cards.TryGetValue(peer.Id,out var card))
        {
            card = new VideoCard(peer); cards[peer.Id] = card; videos.Controls.Add(card);
            card.Stop += () => StopWatching(peer.Id);
            card.Docked += () => { if(!closing && card.Active && WindowState==FormWindowState.Minimized) { pausedViews.Add(peer.Id); room.Watch(peer.Id,IntPtr.Zero,false); } };
        }
        card.Active = true; ArrangeVideos(); room.Watch(peer.Id,card.VideoHandle,true,card.Sound);
    }
    void ArrangeVideos()
    {
        bool showPreview=preview.Checked&&room.Sharing&&WindowState!=FormWindowState.Minimized;
        var visible = cards.Values.Where(c => c.Active).Cast<Control>().Concat(showPreview?new[]{localCard}:Array.Empty<Control>()).ToArray(); videos.Visible = visible.Length > 0; empty.Visible = visible.Length == 0; localCard.Visible=showPreview;
        foreach(var card in cards.Values) card.Visible = card.Active;
        if (visible.Length == 0) return;
        videos.SuspendLayout(); videos.ColumnStyles.Clear(); videos.RowStyles.Clear(); videos.ColumnCount = visible.Length == 1 ? 1 : 2; videos.RowCount = visible.Length > 2 ? 2 : 1;
        for(int i = 0; i < videos.ColumnCount; i++) videos.ColumnStyles.Add(new(SizeType.Percent,100f/videos.ColumnCount));
        for(int i = 0; i < videos.RowCount; i++) videos.RowStyles.Add(new(SizeType.Percent,100f/videos.RowCount));
        for(int i = 0; i < visible.Length; i++) { videos.SetCellPosition(visible[i],new TableLayoutPanelCellPosition(i%videos.ColumnCount,i/videos.ColumnCount)); visible[i].Dock = DockStyle.Fill; }
        videos.Padding=new Padding(Ui.Px(this,5),Ui.Px(this,5),Ui.Px(this,5),Ui.Px(this,room.Sharing?72:5));
        videos.ResumeLayout(); if (videos.Visible) videos.BringToFront(); banner.BringToFront();
    }
    async Task ToggleShare()
    {
        if (!ready || resetting) return;
        if (room.Sharing) { await StopSharing(); return; }
        try { _=MediaCapture.SelectEncoder(); } catch(Exception error) { ShowNotice(error.Message); return; }
        using var picker = new SourceDialog(quality.SelectedItem,streamMode,captureCursor); if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
        try
        {
            currentSource = picker.Selected; sourceLabel.Tag = picker.Audio;
            SelectQuality(picker.SelectedQuality); streamMode=picker.SelectedMode; captureCursor=currentSource.CaptureCursor; SaveSettings();
            sourceLabel.Text = currentSource.Name+"\n\n"+(picker.Audio ? (currentSource.Monitor ? "All system audio" : "Selected app audio") : "Video only");
            await room.Share(currentSource,(Quality)quality.SelectedItem!,picker.Audio); Render(); await UpdateLocalPreview();
        }
        catch(Exception error) { ShowNotice(error.Message); }
    }
    async Task StopSharing() { try { await room.StopSharing(); Render(); } catch(Exception error) { ShowNotice(error.Message); } }
    async Task UpdateLocalPreview()
    {
        if(!ready||closing||resetting) return;
        try { await room.Preview(preview.Checked&&room.Sharing&&WindowState!=FormWindowState.Minimized?localVideo.Handle:IntPtr.Zero); }
        catch(Exception error) { if(!closing) ShowNotice("Local preview: "+error.Message); }
    }
    void SelectQuality(Quality selected)
    {
        updatingQuality=true;
        try
        {
            quality.SelectedIndex=-1; if(quality.Items.Count>Quality.All.Length) quality.Items.RemoveRange(Quality.All.Length,quality.Items.Count-Quality.All.Length);
            int index=Array.FindIndex(Quality.All,q=>q.Width==selected.Width&&q.Height==selected.Height&&q.Fps==selected.Fps&&q.Kbps==selected.Kbps&&q.PrioritizeGame==selected.PrioritizeGame);
            if(index<0) { quality.Items.Add(selected); index=quality.Items.Count-1; }
            quality.SelectedIndex=index;
        }
        finally { updatingQuality=false; }
    }
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
        var selected = (Quality)quality.SelectedItem!;
        stats.UpdateValues(room.Sharing ? $"{(room.Height==0?"Source":room.Height+"p")} / {room.TargetFps} fps" : "Not sharing",$"{mbps:F2} Mbps",encoder,$"{room.Viewers} people");
    }
    void ShowNotice(string message) { state.Text = message; state.ForeColor = Color.FromArgb(234,171,141); }
    void ReadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath) || new FileInfo(SettingsPath).Length > 4096) return;
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var value = json.RootElement;
            if (SetupCrypto.ValidName(value.Text("name",32))) displayName.Text = value.Text("name",32);
            var restored=RestoreQuality(value); streamMode=restored.Mode; SelectQuality(restored.Quality);
            captureCursor=!value.TryGetProperty("cursor",out var cursor)||cursor.ValueKind!=JsonValueKind.False;
            stun = value.Bool("stun"); room.Stun = stun;
        }
        catch { }
    }
    internal static (Quality Quality,string Mode) RestoreQuality(JsonElement value)
    {
        // Old custom entries used index 4, now occupied by Performance.
        // Restore their dimensions unless the saved mode explicitly opts in.
        string mode=value.Text("mode",16) switch { "Performance"=>"Performance","Gaming" or "High quality"=>"High quality","Screenshare"=>"Screenshare",_=>"Custom" };
        if(mode=="Performance") return (Quality.Performance,mode);
        if(value.Int("quality")>=4&&ShareOptions.Resolutions.Contains(value.Int("height"))&&ShareOptions.FrameRates.Contains(value.Int("fps")))
            return (new ShareOptions { Mode=mode,Height=value.Int("height"),Fps=value.Int("fps") }.Quality,mode);
        return (Quality.All[Math.Clamp(value.Int("quality"),0,3)],mode);
    }
    void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath,JsonSerializer.Serialize(new { name = displayName.Text, quality = quality.SelectedIndex, height=quality.SelectedItem?.Height??720, fps=quality.SelectedItem?.Fps??30, mode=streamMode, cursor=captureCursor, stun })); } catch { }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312 && message.WParam.ToInt32() == HotkeyId && room.Sharing) _ = StopSharing();
        base.WndProc(ref message);
    }
}
