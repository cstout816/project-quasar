using System.Buffers.Binary;

namespace Quasar;

public enum ActionKind { Button, Switch, Axis, Pedal, Gear, Tuner }

public sealed record CockpitAction(string Id, string Label, ActionKind Kind, int Offset, int Value);

public static class Cockpit
{
    public static readonly CockpitAction[] Actions = CreateActions();
    private static CockpitAction[] CreateActions()
    {
        (string Id, string Label)[] buttons = [
            ("main", "Main weapon"), ("fire", "Fire"), ("lock", "Lock on"), ("eject", "Eject"),
            ("hatch", "Cockpit hatch"), ("ignition", "Ignition"), ("start", "Start"), ("monitor", "Multi-monitor open / close"),
            ("map", "Map zoom"), ("mode", "Monitor mode"), ("submonitor", "Sub-monitor"), ("zoom-in", "Main monitor zoom in"),
            ("zoom-out", "Main monitor zoom out"), ("fss", "Forecast shooting system"), ("manipulator", "Manipulator"), ("line", "Line color"),
            ("wash", "Washing"), ("extinguisher", "Extinguisher"), ("chaff", "Chaff"), ("detach", "Tank detach"),
            ("override", "Override"), ("night", "Night scope"), ("f1", "Function 1"), ("f2", "Function 2"),
            ("f3", "Function 3"), ("main-select", "Main weapon select"), ("sub-select", "Sub weapon select"), ("magazine", "Magazine change"),
            ("comm1", "Communication 1"), ("comm2", "Communication 2"), ("comm3", "Communication 3"), ("comm4", "Communication 4"),
            ("comm5", "Communication 5"), ("sight-press", "Sight change click"), ("filter", "Filter control"), ("oxygen", "Oxygen supply"),
            ("fuel", "Fuel flow"), ("buffer", "Buffer material"), ("location", "VT location")];
        var actions = buttons.Select((button, index) => new CockpitAction(button.Id, button.Label,
            index >= 34 ? ActionKind.Switch : ActionKind.Button, 2 + index / 8, 1 << (index % 8))).ToList();
        string[] axes = ["Aim X", "Aim Y", "Rotation lever", "Sight change X", "Sight change Y", "Sidestep pedal", "Brake pedal", "Accelerator pedal"];
        string[] ids = ["aim-x", "aim-y", "rotation", "sight-x", "sight-y", "sidestep", "brake", "accelerator"];
        actions.AddRange(axes.Select((label, index) => new CockpitAction(ids[index], label,
            index < 5 ? ActionKind.Axis : ActionKind.Pedal, 8 + index * 2, 0)));
        actions.Add(new("gear-r", "Reverse", ActionKind.Gear, 25, -2));
        actions.Add(new("gear-n", "Neutral", ActionKind.Gear, 25, -1));
        for (int position = 1; position <= 5; position++)
            actions.Add(new($"gear-{position}", $"Gear {position}", ActionKind.Gear, 25, position));
        for (int position = 0; position <= 15; position++)
            actions.Add(new($"tuner-{position}", $"Tuner position {position}", ActionKind.Tuner, 24, position));
        return actions.ToArray();
    }
}

public sealed record Binding(string Action, string Device, string Control, int Direction = 0, bool Invert = false,
    double DeadZone = 0, double Minimum = -1, double Center = 0, double Maximum = 1,
    bool Maintained = false);

public sealed record InputProfile(int Schema, string Name, Binding[] Bindings)
{
    public static InputProfile Load(string path) => Storage.Read<InputProfile>(path).Validate();

    public InputProfile Validate()
    {
        if (Schema != 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Bindings is not { Length: <= 256 })
            throw new InvalidDataException("Invalid input profile schema, name or binding count.");
        foreach (var binding in Bindings)
        {
            var action = Cockpit.Actions.FirstOrDefault(action => action.Id == binding?.Action);
            if (binding is null || action is null || string.IsNullOrWhiteSpace(binding.Device) ||
                string.IsNullOrWhiteSpace(binding.Control) || binding.Device.Length > 100 || binding.Control.Length > 80 ||
                binding.Direction is < -1 or > 1 || binding.DeadZone is < 0 or >= 1 ||
                !(binding.Minimum >= -1 && binding.Minimum < binding.Center &&
                  binding.Center < binding.Maximum && binding.Maximum <= 1) ||
                binding.Maintained && action.Kind is not (ActionKind.Switch or ActionKind.Gear or ActionKind.Tuner))
                throw new InvalidDataException("A binding has an invalid action, source or calibration.");
        }
        return this;
    }

    public string[] Conflicts() => Bindings.GroupBy(binding => (binding.Device, binding.Control))
        .Where(group => group.Select(a => a.Action).Distinct().Count() > 1 &&
            (group.Any(binding => binding.Direction == 0) || group.GroupBy(binding => binding.Direction)
            .Any(direction => direction.Select(a => a.Action).Distinct().Count() > 1)))
        .Select(group => $"{group.Key.Device} / {group.Key.Control}").ToArray();

    public static InputProfile Keyboard() => new(1, "Keyboard and mouse", [
        new("main", "mouse", "Left"), new("fire", "mouse", "Right"), new("lock", "key", "Tab"),
        new("hatch", "key", "H"), new("ignition", "key", "I"), new("start", "key", "Return"),
        new("filter", "key", "D6"), new("oxygen", "key", "D7"), new("fuel", "key", "D8"),
        new("buffer", "key", "D9"), new("location", "key", "D0"),
        new("accelerator", "key", "W"), new("brake", "key", "S"), new("sidestep", "key", "Space"),
        new("rotation", "key", "A", -1), new("rotation", "key", "D", 1),
        new("aim-x", "mouse", "X"), new("aim-y", "mouse", "Y"),
        new("gear-n", "key", "D1"), new("gear-1", "key", "D2"), new("gear-r", "key", "R"),
        new("gear-2", "key", "D3"), new("gear-3", "key", "D4"), new("gear-4", "key", "D5"),
        new("gear-5", "key", "F5"), new("zoom-in", "key", "Add"), new("zoom-out", "key", "Subtract")]);

    public static InputProfile Gamepad(string device = "xinput:0") => new(1, "Xbox gamepad", [
        new("main", device, "A"), new("fire", device, "B"), new("lock", device, "X"), new("hatch", device, "Y"),
        new("ignition", device, "LB"), new("start", device, "Start"),
        new("gear-1", device, "Up"), new("gear-n", device, "Down"), new("gear-r", device, "Left"),
        new("aim-x", device, "RX", DeadZone: 0.12), new("aim-y", device, "RY", Invert: true, DeadZone: 0.12),
        new("rotation", device, "LX", DeadZone: 0.12), new("sight-y", device, "LY", Invert: true, DeadZone: 0.12),
        new("brake", device, "LT", DeadZone: 0.05), new("accelerator", device, "RT", DeadZone: 0.05)]);

    public static InputProfile Joystick(string device) => new(1, "Windows joystick starter", [
        new("main", device, "Button 1"), new("lock", device, "Button 2"),
        new("fire", device, "Button 3"), new("start", device, "Button 4"),
        new("aim-x", device, "X", DeadZone: 0.12), new("aim-y", device, "Y", DeadZone: 0.12)]);
}

public sealed record InputSample(string Device, string Control, double Value, bool Axis = false,
    bool Unipolar = false);
public sealed record InputFrame(HashSet<string> Devices, InputSample[] Samples, byte[]? OriginalReport = null);

public sealed class XidMapper(InputProfile profile)
{
    private readonly Dictionary<Binding, bool> pressed = new();
    private readonly Dictionary<string, string> latched = new();
    private int gear = -1, tuner;
    private string? gearDevice, tunerDevice;
    private bool gearIsMaintained, tunerIsMaintained;
    private bool originalActive;

    public byte[] Map(InputFrame frame)
    {
        if (IsOriginalReport(frame.OriginalReport))
        {
            Reset();
            originalActive = true;
            return (byte[])frame.OriginalReport!.Clone();
        }

        var report = new byte[26];
        report[1] = 26;
        report[9] = report[11] = 128;
        report[25] = 255;
        if (originalActive)
        {
            originalActive = false;
            return report;
        }

        var connected = frame.Devices;
        foreach (var binding in pressed.Keys.Where(binding => !connected.Contains(binding.Device)).ToArray())
        {
            pressed.Remove(binding);
        }
        foreach (var action in latched.Where(pair => !connected.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            latched.Remove(action);
        if (gearDevice is not null && !connected.Contains(gearDevice))
        {
            gear = -1;
            gearDevice = null;
        }
        if (tunerDevice is not null && !connected.Contains(tunerDevice))
        {
            tuner = 0;
            tunerDevice = null;
        }

        var samples = frame.Samples.ToDictionary(sample => (sample.Device, sample.Control));
        bool maintainedGear = false, maintainedTuner = false;
        foreach (var group in profile.Bindings.GroupBy(binding => binding.Action))
        {
            var action = Cockpit.Actions.First(action => action.Id == group.Key);
            double strongest = 0;
            bool digital = false;
            string? switchEdge = null;
            foreach (var binding in group.Distinct().Where(binding => connected.Contains(binding.Device)))
            {
                samples.TryGetValue((binding.Device, binding.Control), out var sample);
                var value = Transform(binding, sample, action.Kind);
                if (Math.Abs(value) > Math.Abs(strongest))
                    strongest = value;
                var held = value > 0.5;
                var rising = pressed.TryGetValue(binding, out var wasHeld) && !wasHeld && held;
                pressed[binding] = held;

                if (action.Kind == ActionKind.Switch && !binding.Maintained)
                {
                    if (rising)
                        switchEdge ??= binding.Device;
                }
                else
                    digital |= held;

                if (action.Kind == ActionKind.Gear && (binding.Maintained ? held : rising))
                {
                    gear = action.Value;
                    gearDevice = binding.Device;
                    gearIsMaintained = binding.Maintained;
                }
                if (action.Kind == ActionKind.Tuner && (binding.Maintained ? held : rising))
                {
                    tuner = action.Value;
                    tunerDevice = binding.Device;
                    tunerIsMaintained = binding.Maintained;
                }
                maintainedGear |= action.Kind == ActionKind.Gear && binding.Maintained && held;
                maintainedTuner |= action.Kind == ActionKind.Tuner && binding.Maintained && held;
            }

            if (switchEdge is not null && !latched.Remove(action.Id))
                latched[action.Id] = switchEdge;
            if (action.Kind is ActionKind.Button or ActionKind.Switch && (digital || latched.ContainsKey(action.Id)))
                report[action.Offset] |= (byte)action.Value;
            if (action.Kind == ActionKind.Axis)
            {
                var raw = action.Offset < 12
                    ? (ushort)Math.Clamp(32768 + Math.Round(strongest * 32768), 0, 65535)
                    : unchecked((ushort)(short)Math.Round(strongest * 32767));
                BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(action.Offset), raw);
            }
            if (action.Kind == ActionKind.Pedal)
                BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(action.Offset),
                    (ushort)(Math.Round(Math.Clamp(strongest, 0, 1) * 255) * 256));
        }

        if (!maintainedGear && gearDevice is not null && gearIsMaintained)
        {
            // A physical lever can sit between detents. Do not latch its last position.
            report[25] = 0;
        }
        else
            report[25] = unchecked((byte)gear);
        report[24] = (byte)tuner;
        if (!maintainedTuner && tunerDevice is not null && tunerIsMaintained)
            report[24] = 0;
        return report;
    }

    public static bool IsOriginalReport(byte[]? report) =>
        report is { Length: 26 } && report[0] == 0 && report[1] == 26;

    private void Reset()
    {
        pressed.Clear();
        latched.Clear();
        gear = -1;
        tuner = 0;
        gearDevice = tunerDevice = null;
        gearIsMaintained = tunerIsMaintained = false;
    }

    private static double Transform(Binding binding, InputSample? sample, ActionKind kind)
    {
        if (sample is null || !double.IsFinite(sample.Value))
            return 0;
        if (!sample.Axis)
            return kind == ActionKind.Axis && binding.Direction == -1 ? -sample.Value : sample.Value;

        var raw = Math.Clamp(sample.Value, binding.Minimum, binding.Maximum);
        double calibrated;
        if (sample.Unipolar)
        {
            calibrated = Math.Clamp((raw - Math.Max(0, binding.Minimum)) /
                Math.Max(0.001, binding.Maximum - Math.Max(0, binding.Minimum)), 0, 1);
            if (binding.Invert)
                calibrated = 1 - calibrated;
        }
        else
        {
            calibrated = raw >= binding.Center ? (raw - binding.Center) / (binding.Maximum - binding.Center)
                : (raw - binding.Center) / (binding.Center - binding.Minimum);
            if (binding.Invert)
                calibrated = -calibrated;
        }
        var value = Math.Sign(calibrated) * Math.Max(0, Math.Abs(calibrated) - binding.DeadZone) / (1 - binding.DeadZone);
        if (kind == ActionKind.Axis && binding.Direction == 0)
            return value;
        var directed = binding.Direction < 0 ? -value : value;
        return Math.Max(0, directed) * (kind == ActionKind.Axis && binding.Direction < 0 ? -1 : 1);
    }
}
