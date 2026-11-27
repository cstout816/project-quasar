using System.Runtime.InteropServices;
using Quasar;

namespace Quasar.Launcher;

internal interface IInputSource : IDisposable
{
    string[] Descriptions { get; }
    string? FirstJoystick { get; }
    InputFrame Poll(bool focused);
}

internal sealed class Devices : IInputSource
{
    private static readonly string[] Axis = ["X", "Y", "Z", "R", "U", "V"];
    private static readonly (ushort Mask, string Name)[] PadButtons = [
        (0x1, "Up"), (0x2, "Down"), (0x4, "Left"), (0x8, "Right"), (0x10, "Start"),
        (0x20, "Back"), (0x40, "LS"), (0x80, "RS"), (0x100, "LB"), (0x200, "RB"),
        (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")];
    private Point? cursor;
    private readonly OriginalController original = new();
    public string[] Descriptions { get; private set; } = [];
    public string? FirstJoystick { get; private set; }

    public InputFrame Poll(bool focused)
    {
        var devices = new HashSet<string>();
        var labels = new List<string>();
        var samples = new List<InputSample>();
        if (focused)
        {
            devices.UnionWith(["key", "mouse"]);
            labels.Add("Keyboard and mouse");
            for (int key = 8; key <= 254; key++)
                if ((GetAsyncKeyState(key) & 0x8000) != 0 && Enum.IsDefined(typeof(Keys), key))
                    samples.Add(new("key", key == 13 ? "Return" : ((Keys)key).ToString(), 1));
            foreach (var pair in new[] { (1, "Left"), (2, "Right"), (4, "Middle"), (5, "X1"), (6, "X2") })
                if ((GetAsyncKeyState(pair.Item1) & 0x8000) != 0)
                    samples.Add(new("mouse", pair.Item2, 1));
            var position = Cursor.Position;
            if (cursor is Point previous)
            {
                samples.Add(new("mouse", "X", Math.Clamp((position.X - previous.X) / 36.0, -1, 1), true));
                samples.Add(new("mouse", "Y", Math.Clamp((position.Y - previous.Y) / 36.0, -1, 1), true));
            }
            cursor = position;
        }
        else
            cursor = null;

        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out var pad) != 0)
                continue;
            var id = $"xinput:{slot}";
            devices.Add(id);
            labels.Add($"Xbox-compatible gamepad {slot + 1} ({id})");
            foreach (var (mask, name) in PadButtons)
                if ((pad.Buttons & mask) != 0)
                    samples.Add(new(id, name, 1));
            samples.AddRange(new InputSample[] {
                new(id, "LX", pad.LX / 32768.0, true), new(id, "LY", pad.LY / 32768.0, true),
                new(id, "RX", pad.RX / 32768.0, true), new(id, "RY", pad.RY / 32768.0, true),
                new(id, "LT", pad.LT / 255.0, true, true), new(id, "RT", pad.RT / 255.0, true, true)
            });
        }

        FirstJoystick = null;
        for (uint slot = 0; slot < Math.Min(joyGetNumDevs(), 16); slot++)
        {
            var data = new JoyInfo { Size = (uint)Marshal.SizeOf<JoyInfo>(), Flags = 0xFF };
            if (joyGetPosEx(slot, ref data) != 0 ||
                joyGetDevCaps((nuint)slot, out var caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0)
                continue;
            var id = $"joy:{caps.Manufacturer:X4}:{caps.Product:X4}:{slot}";
            FirstJoystick ??= id;
            devices.Add(id);
            labels.Add($"{caps.Name} ({id}, Windows joystick)");
            AddAxis(0, data.X, caps.XMin, caps.XMax);
            AddAxis(1, data.Y, caps.YMin, caps.YMax);
            if ((caps.Flags & 1) != 0) AddAxis(2, data.Z, caps.ZMin, caps.ZMax);
            if ((caps.Flags & 2) != 0) AddAxis(3, data.R, caps.RMin, caps.RMax);
            if ((caps.Flags & 4) != 0) AddAxis(4, data.U, caps.UMin, caps.UMax);
            if ((caps.Flags & 8) != 0) AddAxis(5, data.V, caps.VMin, caps.VMax);
            for (int button = 0; button < Math.Min(caps.NumButtons, 32); button++)
                if ((data.Buttons & (1u << button)) != 0)
                    samples.Add(new(id, $"Button {button + 1}", 1));
            if (data.Pov < 36000)
                samples.Add(new(id, $"Hat {(data.Pov + 2250) / 4500 % 8}", 1));

            void AddAxis(int index, uint value, uint minimum, uint maximum)
            {
                if (maximum > minimum)
                    samples.Add(new(id, Axis[index], Math.Clamp(2.0 * (value - (double)minimum) / (maximum - minimum) - 1, -1, 1), true));
            }
        }

        var direct = original.Poll();
        labels.Insert(0, direct.Status);
        Descriptions = labels.ToArray();
        return new(devices, samples.ToArray(), direct.Report);
    }

    public void Dispose() => original.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct PadState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfo
    {
        public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Manufacturer, Product;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint XMin, XMax, YMin, YMax, ZMin, ZMax, NumButtons, PeriodMin, PeriodMax,
            RMin, RMax, UMin, UMax, VMin, VMax, Flags, MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemDriver;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint slot, out PadState state);
    [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint slot, ref JoyInfo info);
    [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCaps(nuint slot, out JoyCaps caps, uint size);
}
