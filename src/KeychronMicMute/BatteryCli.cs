using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KeychronMicMute;

internal static class BatteryCli
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length < 2 || !IsBatteryArgument(args[1])) return;

        var percentage = RawHidBattery.TryReadPercentageAsync().GetAwaiter().GetResult();
        if (percentage is int value)
        {
            CliOutput.WriteLine($"{value}%");
            Environment.Exit(0);
        }

        // Keep stdout/stderr clean so this mode is easy to use in scripts.
        Environment.Exit(2);
    }

    private static bool IsBatteryArgument(string value) =>
        value.Equals("--battery", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("/battery", StringComparison.OrdinalIgnoreCase);
}

internal static class RawHidBattery
{
    private const ushort RawUsagePage = 0xFF60;
    private const ushort RawUsage = 0x0061;
    private const byte ViaCustomGetValue = 0x08;
    private const byte ViaCustomChannel = 0x00;
    private const byte BatteryValueId = 0xB1;
    private const int HidpStatusSuccess = 0x00110000;

    public static async Task<int?> TryReadPercentageAsync()
    {
        foreach (var path in HidNative.EnumerateDevicePaths())
        {
            try
            {
                var value = await TryReadFromDeviceAsync(path).ConfigureAwait(false);
                if (value is not null) return value;
            }
            catch
            {
                // A HID collection may disappear or reject access while enumerating.
            }
        }

        return null;
    }

    private static async Task<int?> TryReadFromDeviceAsync(string path)
    {
        using var handle = HidNative.Open(path);
        if (handle.IsInvalid) return null;

        var caps = HidNative.GetCapabilities(handle);
        if (caps is null || caps.Value.UsagePage != RawUsagePage || caps.Value.Usage != RawUsage) return null;
        if (caps.Value.InputReportLength < 5 || caps.Value.OutputReportLength < 4) return null;

        using var stream = new FileStream(handle, FileAccess.ReadWrite, 64, isAsync: true);
        var request = new byte[caps.Value.OutputReportLength];

        // Windows HID I/O reserves byte 0 for the report ID. QMK Raw HID uses report ID 0.
        request[1] = ViaCustomGetValue;
        request[2] = ViaCustomChannel;
        request[3] = BatteryValueId;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

        var response = new byte[caps.Value.InputReportLength];
        while (!timeout.IsCancellationRequested)
        {
            Array.Clear(response);
            int read;
            try
            {
                read = await stream.ReadAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read < 5) continue;
            if (response[1] != ViaCustomGetValue || response[2] != ViaCustomChannel || response[3] != BatteryValueId) continue;

            var percentage = response[4];
            return percentage <= 100 ? percentage : null;
        }

        return null;
    }

    private static class HidNative
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private static readonly IntPtr InvalidHandleValue = new(-1);

        public static IEnumerable<string> EnumerateDevicePaths()
        {
            HidD_GetHidGuid(out var hidGuid);
            var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (deviceInfoSet == InvalidHandleValue) yield break;

            try
            {
                for (uint index = 0; ; index++)
                {
                    var interfaceData = new SpDeviceInterfaceData
                    {
                        CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
                    };

                    if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    {
                        if (Marshal.GetLastWin32Error() == 259) yield break; // ERROR_NO_MORE_ITEMS
                        continue;
                    }

                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                    if (requiredSize == 0) continue;

                    var detail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detail, requiredSize, out _, IntPtr.Zero)) continue;

                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.DevicePath begins immediately after DWORD cbSize.
                        var path = Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(uint)));
                        if (!string.IsNullOrWhiteSpace(path)) yield return path;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        public static SafeFileHandle Open(string path) => CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        public static HidCapabilities? GetCapabilities(SafeFileHandle handle)
        {
            if (!HidD_GetPreparsedData(handle, out var preparsedData)) return null;

            var capsBuffer = Marshal.AllocHGlobal(64);
            try
            {
                if (HidP_GetCaps(preparsedData, capsBuffer) != HidpStatusSuccess) return null;

                return new HidCapabilities(
                    Usage: unchecked((ushort)Marshal.ReadInt16(capsBuffer, 0)),
                    UsagePage: unchecked((ushort)Marshal.ReadInt16(capsBuffer, 2)),
                    InputReportLength: unchecked((ushort)Marshal.ReadInt16(capsBuffer, 4)),
                    OutputReportLength: unchecked((ushort)Marshal.ReadInt16(capsBuffer, 6)));
            }
            finally
            {
                Marshal.FreeHGlobal(capsBuffer);
                HidD_FreePreparsedData(preparsedData);
            }
        }

        internal readonly record struct HidCapabilities(ushort Usage, ushort UsagePage, ushort InputReportLength, ushort OutputReportLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public uint CbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}

internal static class CliOutput
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void WriteLine(string value)
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            AttachConsole(AttachParentProcess);
            handle = GetStdHandle(StdOutputHandle);
        }

        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

        var bytes = Encoding.ASCII.GetBytes(value + Environment.NewLine);
        WriteFile(handle, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);
}
