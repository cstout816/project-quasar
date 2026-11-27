namespace Quasar.Launcher;

internal sealed partial class LauncherWindow
{
    private readonly Label graphicsStatus = Caption(), requestedDisplay = Caption(), effectiveDisplay = Caption();
    private readonly ComboBox displayMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
    private readonly CockpitCheckBox experimental = new() { Text = "Enable experimental presentation settings", AutoSize = true };
    private readonly CockpitCheckBox widescreen = new() { Text = "16:9 presentation", AutoSize = true };
    private readonly CockpitCheckBox vsync = new() { Text = "VSync", AutoSize = true };
    private readonly CockpitButton original = new() { Text = "Restore Original preset" };
    private readonly NumericUpDown width = Number(640, 640, 7680), height = Number(480, 480, 4320),
        frameCap = Number(0, 0, 240);

    private void BuildGraphics()
    {
        var panel = Page("Graphics");
        var resolution = Caption("640 × 480");
        resolution.Font = new("Consolas", 27);
        resolution.ForeColor = CockpitTheme.Ready;
        displayMode.AccessibleName = "Presentation mode";
        width.AccessibleName = "Output width";
        height.AccessibleName = "Output height";
        frameCap.AccessibleName = "Frame cap";
        panel.Controls.Add(Module("Original preset", resolution,
            Caption("4:3 output. The runtime sets the internal rendering resolution."), graphicsStatus, original));
        panel.Controls.Add(Module("Requested display", Row(Caption("Display mode"), displayMode), requestedDisplay));
        panel.Controls.Add(Module("Effective at launch", effectiveDisplay));
        panel.Controls.Add(Module("Experimental settings", experimental,
            Row(Caption("Output width"), width, Caption("Output height"), height),
            Row(widescreen, vsync, Caption("Frame cap (0 = runtime default)"), frameCap)));
        original.Click += (_, _) => Run(() => ChangeSettings(settings with { Graphics = Graphics.Original }));
        displayMode.SelectedIndexChanged += (_, _) => SaveGraphics(value => value with { Mode = (string?)displayMode.SelectedItem ?? "windowed" });
        experimental.CheckedChanged += (_, _) => SaveGraphics(value => value with { Experimental = experimental.Checked });
        widescreen.CheckedChanged += (_, _) => SaveGraphics(value => value with { Aspect = widescreen.Checked ? "16:9" : "4:3" });
        vsync.CheckedChanged += (_, _) => SaveGraphics(value => value with { VSync = vsync.Checked });
        width.ValueChanged += (_, _) => SaveGraphics(value => value with { Width = (int)width.Value });
        height.ValueChanged += (_, _) => SaveGraphics(value => value with { Height = (int)height.Value });
        frameCap.ValueChanged += (_, _) => SaveGraphics(value => value with { FrameCap = (int)frameCap.Value });
    }

    private void RefreshGraphics()
    {
        refreshing = true;
        try
        {
            bool Supported(string feature) => runtime?.Supports(feature) == true && settingsError is null;
            var supported = Supported("original-640x480");
            var requested = settings.Graphics ?? Graphics.Original;
            var effective = supported
                ? RuntimeLaunch.SupportedGraphics(runtime!, requested) : Graphics.Original;
            requestedDisplay.Text = DisplaySummary(requested);
            effectiveDisplay.Text = supported ? DisplaySummary(effective)
                : "Runtime defaults apply. No graphics settings will be sent.";
            effectiveDisplay.ForeColor = supported ? CockpitTheme.Ready : CockpitTheme.Muted;
            graphicsStatus.Text = supported ? "The runtime declares support for Original and the enabled presentation options."
                : "The selected runtime has no graphics settings contract. Its own graphics defaults apply.";
            displayMode.Items.Clear();
            displayMode.Items.Add("windowed");
            if (Supported("borderless")) displayMode.Items.Add("borderless");
            if (Supported("fullscreen")) displayMode.Items.Add("fullscreen");
            displayMode.SelectedItem = effective.Mode;
            displayMode.Enabled = supported;
            original.Enabled = supported;
            experimental.Enabled = supported && RuntimeManifest.Features.Skip(4).Any(Supported);
            experimental.Checked = requested.Experimental;
            width.Enabled = height.Enabled = effective.Experimental && Supported("output-resolution");
            widescreen.Enabled = effective.Experimental && Supported("widescreen");
            vsync.Enabled = effective.Experimental && Supported("vsync");
            frameCap.Enabled = effective.Experimental && Supported("frame-cap");
            width.Value = Math.Clamp(requested.Width, width.Minimum, width.Maximum);
            height.Value = Math.Clamp(requested.Height, height.Minimum, height.Maximum);
            widescreen.Checked = requested.Aspect == "16:9";
            vsync.Checked = requested.VSync;
            frameCap.Value = Math.Clamp(requested.FrameCap, frameCap.Minimum, frameCap.Maximum);
        }
        finally { refreshing = false; }
    }

    private static string DisplaySummary(Graphics value) =>
        $"{value.Mode}  •  {value.Width} × {value.Height}  •  {value.Aspect}\n" +
        $"VSync {(value.VSync ? "on" : "off")}  •  Frame cap {(value.FrameCap == 0 ? "runtime default" : $"{value.FrameCap} FPS")}";

    private void SaveGraphics(Func<Graphics, Graphics> change)
    {
        if (refreshing)
            return;
        Run(() => ChangeSettings(settings with { Graphics = change(settings.Graphics ?? Graphics.Original) }));
    }
}
