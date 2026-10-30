using System.Runtime.InteropServices;
using System.Text;

namespace HavijScreen.Native;

internal static class Gst
{
    const string Core = "gstreamer-1.0-0.dll", Obj = "gobject-2.0-0.dll", Glib = "glib-2.0-0.dll";
    const string App = "gstapp-1.0-0.dll", Rtc = "gstwebrtc-1.0-0.dll", Sdp = "gstsdp-1.0-0.dll";
    internal const ulong Second = 1_000_000_000, None = ulong.MaxValue;
    internal static string RuntimePath { get; private set; } = "";
    static readonly object InitLock = new();
    static bool initialized;
    static IntPtr loop;
    static Thread? loopThread;
    internal static IntPtr SharedClock { get; private set; }
    internal static readonly int PtsOffset = (int)Marshal.OffsetOf<BufferLayout>(nameof(BufferLayout.Pts));
    internal static readonly int DtsOffset = (int)Marshal.OffsetOf<BufferLayout>(nameof(BufferLayout.Dts));
    internal static readonly int DurationOffset = (int)Marshal.OffsetOf<BufferLayout>(nameof(BufferLayout.Duration));

    [StructLayout(LayoutKind.Sequential)] struct MiniObject
    {
        public UIntPtr Type; public int RefCount, LockState; public uint Flags;
        public IntPtr Copy, Dispose, Free; public uint PrivateCount; public IntPtr Private;
    }
    [StructLayout(LayoutKind.Sequential)] struct BufferLayout
    {
        public MiniObject Mini; public IntPtr Pool; public ulong Pts, Dts, Duration, Offset, OffsetEnd;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MapInfo
    {
        public IntPtr Memory; public int Flags; public IntPtr Data; public UIntPtr Size, MaxSize;
        public IntPtr User0, User1, User2, User3, Reserved0, Reserved1, Reserved2, Reserved3;
    }
    [StructLayout(LayoutKind.Sequential)] struct ErrorInfo { public uint Domain; public int Code; public IntPtr Message; }
    [StructLayout(LayoutKind.Sequential)] internal struct Description { public int Type; public IntPtr Sdp; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Notify(IntPtr sender, IntPtr spec, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ObjectSignal(IntPtr sender, IntPtr item, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VoidSignal(IntPtr sender, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void StringSignal(IntPtr sender, IntPtr text, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void IceSignal(IntPtr sender, uint line, IntPtr candidate, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PromiseChanged(IntPtr promise, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NewSample(IntPtr sink, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int BusMessage(IntPtr bus, IntPtr message, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyNotify(IntPtr data);
    static readonly DestroyNotify ReleasePromise = data => { if (data != IntPtr.Zero) GCHandle.FromIntPtr(data).Free(); };
    internal static IntPtr Promise(PromiseChanged callback)
    {
        var root = GCHandle.Alloc(callback);
        return gst_promise_new_with_change_func(callback,GCHandle.ToIntPtr(root),Marshal.GetFunctionPointerForDelegate(ReleasePromise));
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr AddDllDirectory(string path);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    internal static void Initialize(string? directory = null)
    {
        lock (InitLock)
        {
            if (initialized) return;
            RuntimePath = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "runtime"));
            string bin = Path.Combine(RuntimePath, "bin");
            if (!File.Exists(Path.Combine(bin, Core))) throw new FileNotFoundException("The native media runtime is missing. Extract the complete HavijScreen ZIP.");
            SetDefaultDllDirectories(0x1000); AddDllDirectory(bin); SetDllDirectory(bin);
            NativeLibrary.SetDllImportResolver(typeof(Gst).Assembly, (name, _, _) =>
                File.Exists(Path.Combine(bin, name)) ? NativeLibrary.Load(Path.Combine(bin, name)) : IntPtr.Zero);
            Environment.SetEnvironmentVariable("PATH", bin + ";" + Environment.GetEnvironmentVariable("PATH"));
            Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", "");
            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", Path.Combine(RuntimePath, "lib", "gstreamer-1.0"));
            string scanner = Path.Combine(RuntimePath, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
            if (File.Exists(scanner)) Environment.SetEnvironmentVariable("GST_PLUGIN_SCANNER_1_0", scanner);
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HavijScreen", "Native");
            Directory.CreateDirectory(cache);
            Environment.SetEnvironmentVariable("GST_REGISTRY_1_0", Path.Combine(cache, "registry-1.28.8-x64.bin"));
            gst_init(IntPtr.Zero, IntPtr.Zero);
            SharedClock = gst_system_clock_obtain();
            // GstBuffer's public x64 ABI is used only for timestamps. Validate it
            // before touching any real packet so ABI drift fails safely.
            IntPtr check = gst_buffer_new_allocate(IntPtr.Zero, UIntPtr.Zero, IntPtr.Zero);
            try
            {
                if (PtsOffset != 72 || ReadTime(check, PtsOffset) != None || ReadTime(check, DtsOffset) != None || ReadTime(check, DurationOffset) != None)
                    throw new InvalidOperationException("Unsupported native media buffer layout.");
            }
            finally { gst_mini_object_unref(check); }
            loop = g_main_loop_new(IntPtr.Zero, 0);
            loopThread = new Thread(() => g_main_loop_run(loop)) { IsBackground = true, Name = "Media events" };
            loopThread.Start(); initialized = true;
        }
    }
    internal static string Text(IntPtr value) => value == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(value) ?? "";
    internal static ulong ReadTime(IntPtr buffer, int offset) => unchecked((ulong)Marshal.ReadInt64(buffer, offset));
    internal static void SetTime(IntPtr buffer, int offset, ulong value) => Marshal.WriteInt64(buffer, offset, unchecked((long)value));
    internal static string Error(IntPtr error)
    {
        if (error == IntPtr.Zero) return "Unknown native media error.";
        try { return Text(Marshal.PtrToStructure<ErrorInfo>(error).Message); } finally { g_error_free(error); }
    }
    internal static IntPtr Parse(string description)
    {
        IntPtr result = gst_parse_launch(description, out IntPtr error);
        if (error != IntPtr.Zero || result == IntPtr.Zero)
        {
            if (result != IntPtr.Zero) gst_object_unref(result);
            throw new InvalidOperationException(Error(error));
        }
        return result;
    }
    internal static bool Has(string factory)
    {
        IntPtr found = gst_element_factory_find(factory);
        if (found == IntPtr.Zero) return false;
        gst_object_unref(found); return true;
    }
    internal static IntPtr Named(IntPtr pipeline, string name)
    {
        IntPtr element = gst_bin_get_by_name(pipeline, name);
        return element != IntPtr.Zero ? element : throw new InvalidOperationException("Missing media element: " + name);
    }
    internal static ulong Connect(IntPtr sender, string signal, Delegate handler) => g_signal_connect_data(sender, signal, Marshal.GetFunctionPointerForDelegate(handler), IntPtr.Zero, IntPtr.Zero, 0);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_init(IntPtr argc, IntPtr argv);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_system_clock_obtain();
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_pipeline_use_clock(IntPtr pipeline, IntPtr clock);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gst_clock_get_time(IntPtr clock);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gst_element_get_base_time(IntPtr element);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string pipeline, out IntPtr error);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_parse_bin_from_description([MarshalAs(UnmanagedType.LPUTF8Str)] string description, int ghost, out IntPtr error);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_set_state(IntPtr element, int state);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeout);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_bin_add(IntPtr bin, IntPtr element);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_bin_remove(IntPtr bin, IntPtr element);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_sync_state_with_parent(IntPtr element);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_element_get_static_pad(IntPtr element, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_pad_get_current_caps(IntPtr pad);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_pad_query_caps(IntPtr pad, IntPtr filter);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_pad_link(IntPtr src, IntPtr sink);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_caps_to_string(IntPtr caps);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_caps_from_string([MarshalAs(UnmanagedType.LPUTF8Str)] string caps);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_element_get_bus(IntPtr element);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_bus_set_sync_handler(IntPtr bus, BusMessage? handler, IntPtr data, IntPtr notify);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_message_get_structure(IntPtr message);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_structure_get_name(IntPtr structure);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_object_unref(IntPtr obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_mini_object_ref(IntPtr obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_mini_object_unref(IntPtr obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_sample_get_buffer(IntPtr sample);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_sample_get_caps(IntPtr sample);
    [DllImport(App, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_app_src_set_caps(IntPtr source, IntPtr caps);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_structure_n_fields(IntPtr structure);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_structure_nth_field_name(IntPtr structure,int index);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_value_get_structure(IntPtr value);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_structure_get_string(IntPtr structure,[MarshalAs(UnmanagedType.LPUTF8Str)] string field);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_structure_get_double(IntPtr structure,[MarshalAs(UnmanagedType.LPUTF8Str)] string field,out double value);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_structure_get_uint(IntPtr structure,[MarshalAs(UnmanagedType.LPUTF8Str)] string field,out uint value);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_util_set_object_arg(IntPtr obj,[MarshalAs(UnmanagedType.LPUTF8Str)] string field,[MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_sample_get_segment(IntPtr sample);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gst_segment_to_running_time(IntPtr segment, int format, ulong position);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_bin_recalculate_latency(IntPtr bin);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_buffer_copy(IntPtr buffer);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern UIntPtr gst_buffer_get_size(IntPtr buffer);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_buffer_new_allocate(IntPtr allocator, UIntPtr size, IntPtr parameters);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_buffer_map(IntPtr buffer, out MapInfo info, int flags);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_buffer_unmap(IntPtr buffer, ref MapInfo info);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern UIntPtr gst_buffer_fill(IntPtr buffer, UIntPtr offset, byte[] src, UIntPtr size);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_promise_new_with_change_func(PromiseChanged handler, IntPtr data, IntPtr notify);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_promise_get_reply(IntPtr promise);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_promise_unref(IntPtr promise);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_structure_get_value(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_value_get_boxed(IntPtr value);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_object_ref(IntPtr obj);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_object_unref(IntPtr obj);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, IntPtr callback, IntPtr data, IntPtr destroy, int flags);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_signal_handler_disconnect(IntPtr instance, ulong handler);
    [DllImport(Obj, EntryPoint = "g_object_get", CallingConvention = CallingConvention.Cdecl)] internal static extern void GetInt(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string property, out int value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_object_get", CallingConvention = CallingConvention.Cdecl)] internal static extern void GetPointer(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string property, out IntPtr value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_object_set", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetInt(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string property, int value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_object_set", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetString(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string property, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name", CallingConvention = CallingConvention.Cdecl)] internal static extern void CreateDescription(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string action, IntPtr options, IntPtr promise);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetDescription(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string action, IntPtr description, IntPtr promise);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name", CallingConvention = CallingConvention.Cdecl)] internal static extern void AddIce(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string action, uint index, [MarshalAs(UnmanagedType.LPUTF8Str)] string? candidate);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name", CallingConvention = CallingConvention.Cdecl)] internal static extern void CreateChannel(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string action, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr options, out IntPtr channel);
    [DllImport(App, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);
    [DllImport(App, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_app_src_push_buffer(IntPtr src, IntPtr buffer);
    [DllImport(Sdp, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_sdp_message_new(out IntPtr message);
    [DllImport(Sdp, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_sdp_message_parse_buffer(byte[] data, uint size, IntPtr message);
    [DllImport(Sdp, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_sdp_message_as_text(IntPtr message);
    [DllImport(Sdp, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_sdp_message_free(IntPtr message);
    [DllImport(Rtc, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gst_webrtc_session_description_new(int type, IntPtr sdp);
    [DllImport(Rtc, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_webrtc_session_description_free(IntPtr description);
    [DllImport(Rtc, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_webrtc_data_channel_send_string_full(IntPtr channel, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, out IntPtr error);
    [DllImport(Rtc, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_webrtc_data_channel_close(IntPtr channel);
    [DllImport("gstvideo-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_video_overlay_set_window_handle(IntPtr overlay, UIntPtr handle);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_error_free(IntPtr error);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_free(IntPtr memory);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_main_loop_new(IntPtr context, int running);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_main_loop_run(IntPtr loop);
}

internal sealed class Pipeline : IDisposable
{
    readonly object stateGate = new();
    public IntPtr Handle { get; private set; }
    readonly List<(IntPtr Object, ulong Id, Delegate Handler)> signals = [];
    readonly Gst.BusMessage busCallback;
    IntPtr bus;
    public event Action<string>? Failed;
    public event Action? Ended;
    public Pipeline(string description)
    {
        Handle = Gst.Parse(description); Gst.gst_pipeline_use_clock(Handle,Gst.SharedClock); bus = Gst.gst_element_get_bus(Handle);
        busCallback = (_, message, _) =>
        {
            // GstMessage's public type follows GstMiniObject at offset 64.
            int type = Marshal.ReadInt32(message, 64);
            if ((type & 2) != 0)
            {
                Gst.gst_message_parse_error(message, out var error, out var debug);
                string detail = Gst.Error(error); if (debug != IntPtr.Zero) Gst.g_free(debug);
                ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(detail));
            }
            if ((type & 1) != 0) ThreadPool.QueueUserWorkItem(_ => Ended?.Invoke());
            if ((type & (1<<19)) != 0) ThreadPool.QueueUserWorkItem(_ => { lock(stateGate) { if (Handle != IntPtr.Zero) Gst.gst_bin_recalculate_latency(Handle); } });
            // Drop messages; no unbounded bus queue when nobody is polling.
            Gst.gst_mini_object_unref(message); return 0;
        };
        Gst.gst_bus_set_sync_handler(bus, busCallback, IntPtr.Zero, IntPtr.Zero);
    }
    public void Connect(IntPtr obj, string signal, Delegate callback) => signals.Add((obj, Gst.Connect(obj, signal, callback), callback));
    public void Play()
    {
        if (Gst.gst_element_set_state(Handle, 4) == 0) throw new InvalidOperationException("The native media pipeline could not start.");
    }
    public void Dispose()
    {
        lock(stateGate)
        {
            if (Handle == IntPtr.Zero) return;
            var bindings = signals.ToArray();
            foreach (var entry in bindings) Gst.g_signal_handler_disconnect(entry.Object, entry.Id);
            signals.Clear(); Gst.gst_bus_set_sync_handler(bus, null, IntPtr.Zero, IntPtr.Zero);
            Gst.gst_element_set_state(Handle, 1); Gst.gst_object_unref(bus); Gst.gst_object_unref(Handle);
            bus = Handle = IntPtr.Zero; GC.KeepAlive(bindings); GC.KeepAlive(busCallback);
        }
    }
}
