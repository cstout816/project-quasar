using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Quasar;

namespace Quasar.Launcher;

internal sealed class OriginalController : IDisposable
{
    private sealed record Snapshot(string Status, byte[]? Report = null);
    private const string DeviceId = @"USB\VID_0A7B&PID_D000";
    private static readonly Guid UsbInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private const string Pending = "Physical hardware verification pending.";
    private readonly CancellationTokenSource stop = new();
    private volatile Snapshot current = new("Original controller discovery is starting.");
    private readonly Task worker;

    public OriginalController() => worker = Task.Run(ReadLoop);

    public (string Status, byte[]? Report) Poll()
    {
        var state = current;
        return ($"{state.Status} {Pending}", state.Report);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { ReadConnected(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    current = new($"Original controller unavailable: {error.Message}");
                }
                await Task.Delay(750, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void ReadConnected()
    {
        var ids = DevicesPresent("USB", 1);
        var device = ids.FirstOrDefault(id => id.StartsWith(DeviceId + "\\", StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            current = new("Original controller absent. Use a passive Xbox-controller-to-USB adapter and a WinUSB driver for 0A7B:D000.");
            return;
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + device);
        if (!string.Equals(key?.GetValue("Service") as string, "WinUSB", StringComparison.OrdinalIgnoreCase))
        {
            current = new("Original controller detected. Input and lights require an explicitly installed WinUSB driver.");
            return;
        }

        using var parameters = key?.OpenSubKey("Device Parameters");
        var registered = parameters?.GetValue("DeviceInterfaceGUIDs") ?? parameters?.GetValue("DeviceInterfaceGUID");
        string[] guids = registered switch { string single => [single], string[] multiple => multiple, _ => [] };
        foreach (var guid in guids.Select(text => Guid.TryParse(text, out var value) ? value : Guid.Empty)
            .Append(UsbInterface).Where(value => value != Guid.Empty).Distinct())
        {
            if (CM_Get_Device_Interface_List_Size(out uint size, in guid, device, 0) != 0 || size <= 1 || size > 65536)
                continue;
            var list = new char[size];
            if (CM_Get_Device_Interface_List(in guid, device, list, size, 0) != 0)
                continue;
            foreach (var path in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (ReadInterface(path)) return;
        }
        current = new("WinUSB detected. Input unavailable. Check the installed driver’s device-interface registration and close other mappers. Lights are unavailable.");
    }

    private bool ReadInterface(string path)
    {
        using var file = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (file.IsInvalid || !WinUsb_Initialize(file, out var usb))
            return false;
        try
        {
            bool input = false, lights = false;
            for (byte index = 0; index < 16 && WinUsb_QueryPipe(usb, 0, index, out var pipe); index++)
            {
                input |= pipe.Id == 0x82 && pipe.Type == 3 && pipe.MaximumPacket >= 26;
                lights |= pipe.Id == 0x01 && pipe.Type == 3 && pipe.MaximumPacket >= 22;
            }
            uint timeout = 90;
            if (!input || !WinUsb_SetPipePolicy(usb, 0x82, 3, sizeof(uint), ref timeout))
                return false;
            var lightStatus = lights ? "Indicator-light endpoint detected. Runtime output pending." : "Indicator-light endpoint unavailable.";
            current = new($"Original controller detected. Waiting for XID input. {lightStatus}");
            var buffer = new byte[32];
            while (!stop.IsCancellationRequested)
            {
                if (!WinUsb_ReadPipe(usb, 0x82, buffer, (uint)buffer.Length, out var length, IntPtr.Zero))
                    break;
                var report = length == 26 ? buffer[..26] : null;
                current = XidMapper.IsOriginalReport(report)
                    ? new($"Original controller active and prioritized in preview. {lightStatus}", report)
                    : new($"Original controller sent an invalid XID report. {lightStatus}");
            }
            current = new("Original controller input disconnected or timed out. Preview released.");
            return true;
        }
        finally { WinUsb_Free(usb); }
    }

    private static string[] DevicesPresent(string filter, uint flags)
    {
        if (CM_Get_Device_ID_List_Size(out var size, filter, flags | 0x100) != 0 || size > 262144)
            return [];
        var buffer = new char[size];
        return CM_Get_Device_ID_List(filter, buffer, size, flags | 0x100) == 0
            ? new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    public void Dispose()
    {
        stop.Cancel();
        worker.GetAwaiter().GetResult();
        stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pipe
    {
        public int Type;
        public byte Id;
        public ushort MaximumPacket;
        public byte Interval;
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(out uint size, in Guid guid, string device, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(in Guid guid, string device, [Out] char[] buffer, uint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("winusb.dll")] private static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
    [DllImport("winusb.dll")] private static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll")] private static extern bool WinUsb_QueryPipe(IntPtr usb, byte alternate, byte index, out Pipe info);
    [DllImport("winusb.dll")] private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint size, ref uint value);
    [DllImport("winusb.dll")] private static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, [Out] byte[] buffer, uint size,
        out uint transferred, IntPtr overlapped);
}
