using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KeychronMicMute;

internal static class Program
{
    private const int HotkeyId = 1;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF24 = 0x87;
    private const uint WmHotkey = 0x0312;
    private const uint WmAppSync = 0x8001;
    private const uint WmAppRebind = 0x8002;
    private static uint _mainThreadId;

    [STAThread]
    public static int Main()
    {
        if (!OperatingSystem.IsWindows()) return 1;

        using var mutex = new Mutex(initiallyOwned: true, @"Local\KeychronMicMute", out var isFirstInstance);
        if (!isFirstInstance) return 0;

        try
        {
            Logger.Info("Starting KeychronMicMute.");
            return Run();
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error.", ex);
            Native.MessageBox(IntPtr.Zero,
                $"KeychronMicMute failed to start.\n\n{ex.Message}\n\nLog: {Logger.LogPath}",
                "KeychronMicMute", 0x10);
            return 1;
        }
    }

    private static int Run()
    {
        _mainThreadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PmNoRemove);

        var hr = Native.CoInitializeEx(IntPtr.Zero, Native.CoinitMultithreaded);
        if (hr < 0 && hr != Native.RpcEChangedMode) Marshal.ThrowExceptionForHR(hr);
        var mustUninitializeCom = hr >= 0;

        try
        {
            using var audio = new AudioController(
                () => Native.PostThreadMessage(_mainThreadId, WmAppSync, UIntPtr.Zero, IntPtr.Zero),
                () => Native.PostThreadMessage(_mainThreadId, WmAppRebind, UIntPtr.Zero, IntPtr.Zero));

            Safe("initial audio bind", audio.Rebind);
            Safe("initial indicator sync", audio.SyncScrollLock);

            if (!Native.RegisterHotKey(IntPtr.Zero, HotkeyId, ModNoRepeat, VkF24))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register global F24 hotkey.");

            Logger.Info("Ready. F24 toggles default Console + Communications capture endpoints.");

            try
            {
                while (true)
                {
                    var result = Native.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");

                    switch (msg.message)
                    {
                        case WmHotkey when msg.wParam.ToUInt64() == HotkeyId:
                            Safe("toggle mute", () => audio.ToggleWithRebindRetry());
                            Safe("indicator sync after toggle", audio.SyncScrollLock);
                            break;
                        case WmAppSync:
                            Safe("mute-state sync", audio.SyncScrollLock);
                            break;
                        case WmAppRebind:
                            Safe("audio-device rebind", audio.Rebind);
                            Safe("indicator sync after rebind", audio.SyncScrollLock);
                            break;
                    }
                }
            }
            finally
            {
                Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
            }
        }
        finally
        {
            if (mustUninitializeCom) Native.CoUninitialize();
        }

        return 0;
    }

    private static void Safe(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error($"Failed to {operation}.", ex); }
    }
}

internal sealed class AudioController : IDisposable
{
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid EventContext = new("6F3F5528-4CFD-46B2-8E26-D4024937E664");

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly DeviceNotificationClient _deviceNotification;
    private readonly Action _onMuteChanged;
    private List<EndpointBinding> _endpoints = [];
    private bool? _lastLoggedMuted;
    private bool _disposed;

    public AudioController(Action onMuteChanged, Action onDefaultDeviceChanged)
    {
        _onMuteChanged = onMuteChanged;
        var type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator, throwOnError: true)!;
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        _deviceNotification = new DeviceNotificationClient(onDefaultDeviceChanged);
        HResult.ThrowIfFailed(_enumerator.RegisterEndpointNotificationCallback(_deviceNotification));
    }

    public void Rebind()
    {
        var next = new Dictionary<string, EndpointBinding>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var role in new[] { ERole.eConsole, ERole.eCommunications })
            {
                if (!TryGetDefaultCaptureEndpoint(role, out var device, out var id)) continue;
                if (next.TryGetValue(id, out var existing))
                {
                    existing.Roles.Add(role);
                    Marshal.FinalReleaseComObject(device);
                    continue;
                }

                var iid = IidAudioEndpointVolume;
                HResult.ThrowIfFailed(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var activated));
                var volume = (IAudioEndpointVolume)activated;
                var callback = new EndpointVolumeCallback(_onMuteChanged);
                HResult.ThrowIfFailed(volume.RegisterControlChangeNotify(callback));
                next.Add(id, new EndpointBinding(id, device, volume, callback, role));
            }
        }
        catch
        {
            foreach (var endpoint in next.Values) endpoint.Dispose();
            throw;
        }

        var old = _endpoints;
        _endpoints = next.Values.ToList();
        foreach (var endpoint in old) endpoint.Dispose();
        _lastLoggedMuted = null;
        Logger.Info($"Bound {_endpoints.Count} capture endpoint(s): {DescribeEndpoints()}.");
    }

    public bool EffectiveMuted => _endpoints.Count > 0 && _endpoints.All(x => x.GetMute());

    public void ToggleWithRebindRetry()
    {
        try { Toggle(); }
        catch
        {
            Rebind();
            Toggle();
        }
    }

    private void Toggle()
    {
        if (_endpoints.Count == 0)
        {
            Logger.Warn("No default capture endpoint is available.");
            return;
        }

        var targetMute = !EffectiveMuted;
        foreach (var endpoint in _endpoints) endpoint.SetMute(targetMute, EventContext);
        Logger.Info(targetMute ? "Muted." : "Unmuted.");
    }

    public void SyncScrollLock()
    {
        var muted = EffectiveMuted;
        ScrollLockIndicator.Set(muted);
        if (_lastLoggedMuted != muted)
        {
            Logger.Info($"Effective Core Audio state: {(muted ? "MUTED" : "UNMUTED")}.");
            _lastLoggedMuted = muted;
        }
    }

    private string DescribeEndpoints() => _endpoints.Count == 0
        ? "none"
        : string.Join(", ", _endpoints.Select(x => $"{string.Join('+', x.Roles.OrderBy(r => r))}:{x.Id}"));

    private bool TryGetDefaultCaptureEndpoint(ERole role, out IMMDevice device, out string id)
    {
        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, role, out device);
        if (hr < 0)
        {
            device = null!;
            id = string.Empty;
            return false;
        }
        HResult.ThrowIfFailed(device.GetId(out id));
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var endpoint in _endpoints) endpoint.Dispose();
        _endpoints.Clear();
        _enumerator.UnregisterEndpointNotificationCallback(_deviceNotification);
        if (Marshal.IsComObject(_enumerator)) Marshal.FinalReleaseComObject(_enumerator);
    }
}

internal sealed class EndpointBinding : IDisposable
{
    public string Id { get; }
    public HashSet<ERole> Roles { get; } = [];
    private readonly IMMDevice _device;
    private readonly IAudioEndpointVolume _volume;
    private readonly EndpointVolumeCallback _callback;
    private bool _disposed;

    public EndpointBinding(string id, IMMDevice device, IAudioEndpointVolume volume, EndpointVolumeCallback callback, ERole initialRole)
    {
        Id = id; _device = device; _volume = volume; _callback = callback; Roles.Add(initialRole);
    }

    public bool GetMute() { HResult.ThrowIfFailed(_volume.GetMute(out var muted)); return muted != 0; }
    public void SetMute(bool muted, Guid context) { HResult.ThrowIfFailed(_volume.SetMute(muted, ref context)); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volume.UnregisterControlChangeNotify(_callback);
        if (Marshal.IsComObject(_volume)) Marshal.FinalReleaseComObject(_volume);
        if (Marshal.IsComObject(_device)) Marshal.FinalReleaseComObject(_device);
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class EndpointVolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action _onChanged;
    public EndpointVolumeCallback(Action onChanged) => _onChanged = onChanged;
    public int OnNotify(IntPtr notifyData) { _onChanged(); return 0; }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceNotificationClient : IMMNotificationClient
{
    private readonly Action _onChanged;
    public DeviceNotificationClient(Action onChanged) => _onChanged = onChanged;
    public int OnDeviceStateChanged(string id, uint state) { _onChanged(); return 0; }
    public int OnDeviceAdded(string id) { _onChanged(); return 0; }
    public int OnDeviceRemoved(string id) { _onChanged(); return 0; }
    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
    {
        if (flow == EDataFlow.eCapture && (role == ERole.eConsole || role == ERole.eCommunications)) _onChanged();
        return 0;
    }
    public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
}

internal static class ScrollLockIndicator
{
    private const int VkScroll = 0x91;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    public static void Set(bool enabled)
    {
        var current = (Native.GetKeyState(VkScroll) & 1) != 0;
        if (current == enabled) return;

        var inputs = new[]
        {
            new Input { type = InputKeyboard, U = new InputUnion { ki = new KeyboardInput { wVk = VkScroll } } },
            new Input { type = InputKeyboard, U = new InputUnion { ki = new KeyboardInput { wVk = VkScroll, dwFlags = KeyeventfKeyup } } }
        };
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != (uint)inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed while setting Scroll Lock state.");
    }
}

internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeychronMicMute");
    public static string LogPath { get; } = Path.Combine(DirectoryPath, "helper.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{level}] {message}{(ex is null ? "" : $" {ex}")}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < 1_000_000) return;
        var old = LogPath + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(LogPath, old);
    }
}

internal static class HResult { public static void ThrowIfFailed(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); } }

public enum EDataFlow { eRender, eCapture, eAll, EDataFlow_enum_count }
public enum ERole { eConsole, eMultimedia, eCommunications, ERole_enum_count }
[Flags] internal enum ClsCtx : uint { InprocServer = 0x1, InprocHandler = 0x2, LocalServer = 0x4, RemoteServer = 0x10, All = InprocServer | InprocHandler | LocalServer | RemoteServer }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint mask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, ClsCtx ctx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute(out int mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(ref Guid context);
    [PreserveSig] int VolumeStepDown(ref Guid context);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid fmtid; public uint pid; }
[StructLayout(LayoutKind.Sequential)] internal struct Point { public int x; public int y; }
[StructLayout(LayoutKind.Sequential)] internal struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public Point pt; public uint lPrivate; }
[StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public InputUnion U; }
[StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput mi; [FieldOffset(0)] public KeyboardInput ki; [FieldOffset(0)] public HardwareInput hi; }
[StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] internal struct HardwareInput { public uint uMsg; public ushort wParamL; public ushort wParamH; }

internal static class Native
{
    public const uint PmNoRemove = 0;
    public const uint CoinitMultithreaded = 0;
    public const int RpcEChangedMode = unchecked((int)0x80010106);
    [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll")] public static extern void CoUninitialize();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short GetKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
