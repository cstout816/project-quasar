using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Quasar.Launcher;

internal static class CockpitTheme
{
    public static bool HighContrast => SystemInformation.HighContrast;
    public static Color Background => HighContrast ? SystemColors.Window : Color.FromArgb(20, 24, 28);
    public static Color Panel => HighContrast ? SystemColors.Window : Color.FromArgb(33, 40, 47);
    public static Color Field => HighContrast ? SystemColors.Window : Color.FromArgb(24, 29, 35);
    public static Color Rule => HighContrast ? SystemColors.WindowText : Color.FromArgb(62, 73, 83);
    public static Color Text => HighContrast ? SystemColors.WindowText : Color.FromArgb(236, 239, 224);
    public static Color Muted => HighContrast ? SystemColors.WindowText : Color.FromArgb(181, 192, 182);
    public static Color Amber => HighContrast ? SystemColors.Highlight : Color.FromArgb(237, 177, 82);
    public static Color Alert => HighContrast ? SystemColors.WindowText : Color.FromArgb(255, 201, 119);
    public static Color Ready => HighContrast ? SystemColors.WindowText : Color.FromArgb(177, 213, 148);
    public static int Logical(Control control, int pixels) =>
        (int)Math.Round(pixels * ((control.FindForm() as LauncherWindow)?.FixtureDpi ?? control.DeviceDpi) / 96.0);

    public static void StyleWindowFrame(IntPtr handle)
    {
        if (HighContrast || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        const int darkMode = 20;
        const int captionColor = 35;
        const int textColor = 36;
        int enabled = 1;
        int caption = ColorTranslator.ToWin32(Field);
        int text = ColorTranslator.ToWin32(Text);
        DwmSetWindowAttribute(handle, darkMode, in enabled, sizeof(int));
        DwmSetWindowAttribute(handle, captionColor, in caption, sizeof(int));
        DwmSetWindowAttribute(handle, textColor, in text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in int value, int size);

    public static void Skin(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case CockpitButton button:
                    button.ApplyStyle();
                    break;
                case TextBox textBox:
                    textBox.BackColor = Field;
                    textBox.ForeColor = Text;
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListBox list:
                    list.BackColor = Field;
                    list.ForeColor = Text;
                    list.BorderStyle = BorderStyle.FixedSingle;
                    if (!HighContrast)
                    {
                        list.DrawMode = DrawMode.OwnerDrawFixed;
                        list.ItemHeight = list.Font.Height + 4;
                        list.DrawItem += DrawListItem;
                    }
                    break;
                case ComboBox combo:
                    combo.BackColor = Field;
                    combo.ForeColor = Text;
                    combo.FlatStyle = FlatStyle.Flat;
                    break;
                case TreeView tree:
                    tree.BackColor = Field;
                    tree.ForeColor = Text;
                    tree.LineColor = Rule;
                    break;
                case NumericUpDown:
                    control.BackColor = Field;
                    control.ForeColor = Text;
                    break;
                case CheckBox checkbox:
                    checkbox.ForeColor = Text;
                    checkbox.Padding = new(3, 4, 3, 4);
                    break;
            }
            Skin(control);
        }
    }

    private static void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count)
            return;
        var selected = e.State.HasFlag(DrawItemState.Selected);
        using var fill = new SolidBrush(selected ? Color.FromArgb(65, 53, 35) : Field);
        e.Graphics.FillRectangle(fill, e.Bounds);
        TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), list.Font,
            Rectangle.Inflate(e.Bounds, -6, 0), selected ? Alert : Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && e.State.HasFlag(DrawItemState.Focus))
            ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, Alert, Field);
    }
}

internal sealed class CockpitButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsPrimary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsNavigation { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Active { get; set; }
    private bool hovered;
    private bool pressed;

    public void ApplyStyle()
    {
        FlatStyle = FlatStyle.Flat;
        Font = new("Segoe UI Semibold", IsPrimary ? 12 : 10);
        AutoSize = !IsNavigation;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = Size.Empty;
        if (IsNavigation)
            Margin = Padding.Empty;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (IsNavigation)
            return base.GetPreferredSize(proposedSize);
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new(
            Math.Max(CockpitTheme.Logical(this, IsPrimary ? 132 : 55), text.Width + CockpitTheme.Logical(this, IsPrimary ? 30 : 18)),
            Math.Max(CockpitTheme.Logical(this, IsPrimary ? 42 : 33), text.Height + CockpitTheme.Logical(this, 10)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (CockpitTheme.HighContrast)
        {
            base.OnPaint(e);
            return;
        }

        bool amber = IsPrimary && Enabled;
        var fill = amber ? (pressed ? Color.FromArgb(202, 147, 65) : hovered ? CockpitTheme.Alert : CockpitTheme.Amber)
            : Enabled && hovered ? Color.FromArgb(46, 55, 54)
            : IsNavigation && !Active ? CockpitTheme.Field : CockpitTheme.Panel;
        var ink = amber ? CockpitTheme.Background : Active ? CockpitTheme.Alert
            : Enabled ? CockpitTheme.Text : Color.FromArgb(148, 159, 152);
        using var background = new SolidBrush(fill);
        e.Graphics.FillRectangle(background, ClientRectangle);
        if (IsNavigation)
        {
            using var edge = new SolidBrush(Active ? CockpitTheme.Amber : CockpitTheme.Rule);
            int line = Active ? CockpitTheme.Logical(this, 3) : 1;
            if (Active)
                e.Graphics.FillRectangle(edge, 0, 0, line, Height);
            else
                e.Graphics.FillRectangle(edge, 0, Height - 1, Width, 1);
        }
        else
        {
            using var edge = new Pen(amber ? CockpitTheme.Amber : CockpitTheme.Rule);
            e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
}

internal sealed class CockpitCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (CockpitTheme.HighContrast)
        {
            base.OnPaint(e);
            return;
        }
        var ink = Enabled ? CockpitTheme.Text : Color.FromArgb(148, 159, 152);
        e.Graphics.Clear(BackColor);
        int size = CockpitTheme.Logical(this, 13);
        var box = new Rectangle(Padding.Left + 1, (Height - size) / 2, size, size);
        using var mark = new Pen(Enabled && Checked ? CockpitTheme.Amber : ink);
        e.Graphics.DrawRectangle(mark, box);
        if (Checked)
        {
            mark.Width = CockpitTheme.Logical(this, 2);
            e.Graphics.DrawLines(mark, (Point[])[new(box.Left + size / 5, box.Top + size / 2),
                new(box.Left + size / 2, box.Bottom - size / 5), new(box.Right - size / 6, box.Top + size / 4)]);
        }
        int start = box.Right + CockpitTheme.Logical(this, 6);
        var label = new Rectangle(start, 0, Math.Max(0, Width - start), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, label, ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(label, -1, -2), ink, BackColor);
    }
}

internal sealed class CockpitPages : UserControl
{
    private readonly FlowLayoutPanel navigation = new()
    {
        Dock = DockStyle.Left, Width = 135, Padding = new(8, 15, 8, 0),
        FlowDirection = FlowDirection.TopDown, WrapContents = false,
        BackColor = CockpitTheme.Field, AccessibleName = "Launcher views"
    };
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly List<Panel> panels = new();
    private readonly List<CockpitButton> buttons = new();
    private int selected = -1;
    public IReadOnlyList<Panel> TabPages => panels;
    public Panel? SelectedTab => selected < 0 ? null : panels[selected];
    public event EventHandler? SelectedIndexChanged;

    public CockpitPages()
    {
        BackColor = CockpitTheme.Field;
        Dock = DockStyle.Fill;
        TabStop = false;
        Controls.Add(content);
        Controls.Add(navigation);
        navigation.Layout += (_, _) =>
        {
            foreach (var button in buttons)
                button.Size = new(Math.Max(1, navigation.ClientSize.Width - navigation.Padding.Horizontal),
                    CockpitTheme.Logical(this, 46));
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex
    {
        get => selected;
        set
        {
            if (value < 0 || value >= panels.Count)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (value == selected)
                return;
            if (selected >= 0)
            {
                panels[selected].Hide();
                MarkButton(buttons[selected], false);
            }
            selected = value;
            panels[selected].Show();
            panels[selected].BringToFront();
            MarkButton(buttons[selected], true);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Add(string label, Panel page)
    {
        int index = panels.Count;
        var button = new CockpitButton
        {
            Text = label, Width = 119, Height = 46, IsNavigation = true, AccessibleRole = AccessibleRole.PageTab
        };
        button.Click += (_, _) => SelectedIndex = index;
        buttons.Add(button);
        navigation.Controls.Add(button);
        page.Text = label;
        page.Dock = DockStyle.Fill;
        page.Hide();
        panels.Add(page);
        content.Controls.Add(page);
        if (selected < 0)
            SelectedIndex = 0;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        navigation.Width = CockpitTheme.Logical(this, 135);
        base.OnLayout(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (panels.Count > 1 && (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab)))
        {
            int direction = keyData.HasFlag(Keys.Shift) ? -1 : 1;
            SelectedIndex = (SelectedIndex + direction + panels.Count) % panels.Count;
            buttons[SelectedIndex].Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static void MarkButton(CockpitButton button, bool active)
    {
        button.Active = active;
        button.AccessibleDescription = active ? "Current view" : null;
        button.Invalidate();
    }
}

internal sealed class ResponsiveSplit : TableLayoutPanel
{
    private readonly float leftShare;
    private readonly Control left, right;
    private bool? stacked;

    public ResponsiveSplit(float leftShare, Control left, Control right)
    {
        this.leftShare = leftShare;
        this.left = left;
        this.right = right;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new(0, 0, 0, 12);
        left.Dock = right.Dock = DockStyle.Fill;
        Controls.Add(left);
        Controls.Add(right);
        Arrange(1000);
    }

    internal void Arrange(int available)
    {
        var next = available < CockpitTheme.Logical(this, 770);
        if (stacked == next)
            return;
        stacked = next;
        SuspendLayout();
        try
        {
            var gap = CockpitTheme.Logical(this, 12);
            ColumnStyles.Clear();
            RowStyles.Clear();
            SetCellPosition(left, new(0, 0));
            SetCellPosition(right, next ? new(0, 1) : new(1, 0));
            ColumnCount = next ? 1 : 2;
            RowCount = next ? 2 : 1;
            ColumnStyles.Add(new(SizeType.Percent, next ? 100 : leftShare));
            if (!next)
                ColumnStyles.Add(new(SizeType.Percent, 100 - leftShare));
            left.Margin = next ? new(0, 0, 0, gap) : new(0, 0, gap, 0);
            right.Margin = Padding.Empty;
        }
        finally { ResumeLayout(false); }
    }
}

internal sealed class ModulePanel : FlowLayoutPanel
{
    public ModulePanel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = CockpitTheme.Panel;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Padding = new(15, 10, 15, 12);
        Margin = new(0, 0, 0, 12);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(CockpitTheme.Rule);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var edge = new SolidBrush(CockpitTheme.Amber);
        e.Graphics.FillRectangle(edge, 0, 0, CockpitTheme.Logical(this, 2),
            Math.Min(ClientSize.Height, CockpitTheme.Logical(this, 30)));
    }
}

internal sealed class CockpitHeader : Panel
{
    public CockpitHeader()
    {
        Dock = DockStyle.Top;
        Height = 89;
        BackColor = CockpitTheme.Field;
        var title = new Label
        {
            Text = "Project Quasar", AutoSize = true, Location = new(74, 10),
            Font = new("Segoe UI Semibold", 26), ForeColor = CockpitTheme.Text
        };
        var subtitle = new Label
        {
            Text = "STEEL BATTALION  /  WINDOWS LAUNCHER", AutoSize = true, Location = new(78, 56),
            Font = new("Consolas", 10), ForeColor = CockpitTheme.Alert
        };
        Controls.Add(title);
        Controls.Add(subtitle);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var edge = new Pen(CockpitTheme.Rule);
        using var accent = new Pen(CockpitTheme.Amber);
        using var icon = QuasarMark.LoadIcon();
        int iconSize = CockpitTheme.Logical(this, 40);
        e.Graphics.DrawIcon(icon, new Rectangle(CockpitTheme.Logical(this, 21), (Height - iconSize) / 2, iconSize, iconSize));
        e.Graphics.DrawLine(edge, 0, Height - 1, Width, Height - 1);
        int left = Width - CockpitTheme.Logical(this, 196);
        int bottom = Height - CockpitTheme.Logical(this, 22);
        int top = CockpitTheme.Logical(this, 22);
        int spacing = CockpitTheme.Logical(this, 14);
        for (int index = 0; index < 9; index++)
            e.Graphics.DrawLine(index == 0 ? accent : edge, left + index * spacing, bottom,
                left + index * spacing + CockpitTheme.Logical(this, 34), top);
    }
}

internal sealed class TacticalDisplay : Control
{
    public TacticalDisplay()
    {
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Wireframe illustration of a fictional Vertical Tank";
        MinimumSize = new(230, 178);
        Margin = new(0);
        Dock = DockStyle.Fill;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width < 2 || ClientSize.Height < 2)
            return;

        var graphics = e.Graphics;
        using var field = new LinearGradientBrush(ClientRectangle, CockpitTheme.Field, CockpitTheme.Panel, 27);
        graphics.FillRectangle(field, ClientRectangle);
        using var border = new Pen(CockpitTheme.Rule);
        graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var grid = new Pen(Color.FromArgb(64, CockpitTheme.Muted));
        var interval = Math.Max(1, CockpitTheme.Logical(this, 24));
        for (int x = Width / 2 % interval; x < Width - 1; x += interval)
            graphics.DrawLine(grid, x, 1, x, Height - 2);
        for (int y = Height / 2 % interval; y < Height - 1; y += interval)
            graphics.DrawLine(grid, 1, y, Width - 2, y);

        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Min(Width / 330f, Height / 180f);
        graphics.TranslateTransform(Width / 2f, Height / 2f);
        graphics.ScaleTransform(scale, scale);
        using var trace = new Pen(CockpitTheme.Ready, 1.35f);
        using var glow = new Pen(Color.FromArgb(90, CockpitTheme.Ready));
        using var target = new Pen(CockpitTheme.Amber, 1.1f);
        using var body = new SolidBrush(CockpitTheme.Field);

        graphics.DrawEllipse(glow, -71, -71, 142, 142);
        graphics.DrawEllipse(glow, -59, -59, 118, 118);
        graphics.DrawLine(glow, -102, 0, 102, 0);
        graphics.DrawLine(glow, 0, -79, 0, 79);
        Outline([new(-22, -63), new(19, -63), new(32, -51), new(28, -30), new(-31, -30), new(-34, -52)]);
        Outline([new(-66, -35), new(-33, -44), new(-24, -32), new(24, -32), new(34, -44), new(69, -35),
            new(76, -3), new(47, 16), new(24, 15), new(13, 25), new(-14, 25), new(-24, 15), new(-46, 16), new(-75, -3)]);
        Outline([new(-48, 14), new(-24, 13), new(-19, 36), new(-27, 60), new(-26, 68), new(-62, 68),
            new(-62, 58), new(-52, 45)]);
        Outline([new(48, 14), new(24, 13), new(19, 36), new(27, 60), new(26, 68), new(62, 68),
            new(62, 58), new(52, 45)]);
        Outline([new(-66, -26), new(-83, -20), new(-93, 39), new(-79, 43), new(-67, 17)]);
        Outline([new(66, -26), new(85, -25), new(101, 28), new(86, 33), new(68, 12)]);
        graphics.DrawLines(trace, [new(-53, -22), new(-37, -26), new(-27, -16), new(-27, 3), new(-51, 4)]);
        graphics.DrawLines(trace, [new(53, -22), new(37, -26), new(27, -16), new(27, 3), new(51, 4)]);
        graphics.DrawLines(trace, [new(-10, -26), new(-10, 5), new(10, 5), new(10, -26)]);
        graphics.DrawLine(target, -16, -49, 16, -49);
        graphics.DrawLine(target, -8, -2, 8, -2);
        for (int side = -1; side <= 1; side += 2)
        {
            graphics.DrawLines(target, [new(side * 112, -31), new(side * 119, -31), new(side * 119, -17)]);
            graphics.DrawLines(target, [new(side * 112, 31), new(side * 119, 31), new(side * 119, 17)]);
        }
        graphics.Restore(state);

        void Outline(Point[] points)
        {
            graphics.FillPolygon(body, points);
            graphics.DrawPolygon(trace, points);
        }
    }
}
