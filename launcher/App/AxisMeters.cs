using System.Buffers.Binary;

namespace Quasar.Launcher;

internal sealed class AxisMeters : Control
{
    private static readonly string[] Names = ["Aim X", "Aim Y", "Rotation", "Sight X", "Sight Y", "Sidestep", "Brake", "Accelerator"];
    private readonly int[] values = new int[Names.Length];

    public AxisMeters()
    {
        Height = 162;
        Width = 245;
        DoubleBuffered = true;
        TabStop = false;
        AccessibleName = "Live mapped axis and pedal meters";
        Reset();
    }

    public void Reset()
    {
        Array.Clear(values);
        Describe();
        Invalidate();
    }

    public void UpdateReport(ReadOnlySpan<byte> report)
    {
        var changed = false;
        for (var index = 0; index < values.Length; index++)
        {
            int raw = BinaryPrimitives.ReadUInt16LittleEndian(report[(8 + index * 2)..]);
            int position = index switch
            {
                < 2 => (raw - 32768) * 100 / 32768,
                < 5 => unchecked((short)raw) * 100 / 32767,
                _ => raw * 100 / 65280
            };
            if (position == values[index])
                continue;
            values[index] = Math.Clamp(position, -100, 100);
            changed = true;
        }
        if (changed)
        {
            Describe();
            Invalidate();
        }
    }

    private void Describe() => AccessibleDescription = string.Join(", ", Names.Zip(values, (name, value) => $"{name} {value}%"));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var track = new SolidBrush(CockpitTheme.Field);
        using var fill = new SolidBrush(CockpitTheme.Amber);
        using var tick = new Pen(CockpitTheme.Muted);
        int row = Math.Max(1, Height / Names.Length);
        int textWidth = CockpitTheme.Logical(this, 82);
        int scaleWidth = Math.Max(1, Width - textWidth - CockpitTheme.Logical(this, 44));
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        for (var index = 0; index < Names.Length; index++)
        {
            int top = row * index;
            int barHeight = Math.Max(2, row / 3);
            var bar = new Rectangle(textWidth, top + (row - barHeight) / 2, scaleWidth, barHeight);
            e.Graphics.FillRectangle(track, bar);
            int zero = index < 5 ? bar.X + bar.Width / 2 : bar.X;
            int end = zero + (index < 5 ? bar.Width / 2 : bar.Width) * values[index] / 100;
            if (end != zero)
                e.Graphics.FillRectangle(fill, Math.Min(zero, end), bar.Y, Math.Abs(end - zero), bar.Height);
            e.Graphics.DrawLine(tick, zero, bar.Y, zero, bar.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, Names[index], Font, new Rectangle(0, top, textWidth, row), CockpitTheme.Muted, flags);
            TextRenderer.DrawText(e.Graphics, $"{values[index]}%", Font,
                new Rectangle(bar.Right, top, Math.Max(0, Width - bar.Right), row), CockpitTheme.Text, flags | TextFormatFlags.Right);
        }
    }
}
