namespace Quasar.Launcher;

internal sealed partial class LauncherWindow
{
    private readonly Label profileUse = Caption(), deviceList = Caption(), reportPreview = Caption(),
        bindingStatus = Caption(), inputPreview = Caption();
    private readonly TextBox profileName = new() { Width = 215, MaxLength = 80 };
    private readonly ComboBox profileChoice = new() { Width = 255, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox actionSearch = new() { Width = 245, PlaceholderText = "Search cockpit actions" };
    private readonly TreeView actions = new()
    {
        Width = 255, Height = 210, HideSelection = false, FullRowSelect = true, ShowNodeToolTips = true
    };
    private readonly ListBox bindings = new() { Width = 365, Height = 210 };
    private readonly AxisMeters axisMeters = new();
    private readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 35 };
    private IInputSource? devices;
    private XidMapper? mapper;
    private InputSample[] latest = [];
    private HashSet<(string Device, string Control)>? captureBlocked;
    private bool selectingProfile;

    private CockpitAction? SelectedAction => actions.SelectedNode?.Tag as CockpitAction;

    private void BuildControls()
    {
        var panel = Page("Controls");
        profileName.AccessibleName = "Active profile name";
        profileChoice.AccessibleName = "Switch input profile";
        actionSearch.AccessibleName = "Search cockpit actions";
        actions.AccessibleName = "Cockpit actions";
        bindings.AccessibleName = "Bindings for the selected cockpit action";
        reportPreview.Font = new("Consolas", 10);
        reportPreview.ForeColor = CockpitTheme.Ready;
        var joystick = Button("Joystick starter", () => SaveProfiles(profiles.Add(
            InputProfile.Joystick(devices?.FirstJoystick ??
                throw new InvalidOperationException("Connect a Windows joystick to use this starter.")))));
        panel.Controls.Add(Module("Input profile", profileUse,
            Row(profileChoice, Button("Import…", ImportProfile), Button("Export…", ExportProfile)),
            Row(profileName, Button("Rename", () => SetProfile(profile with { Name = profileName.Text.Trim() }))),
            Row(Button("Keyboard / mouse", () => SaveProfiles(profiles.Add(InputProfile.Keyboard()))),
                Button("Xbox defaults", () => SaveProfiles(profiles.Add(InputProfile.Gamepad()))),
                joystick, Button("New blank profile", () => SaveProfiles(profiles.Add(new(1, "Mixed devices", [])))))));
        actions.AfterSelect += (_, _) =>
        {
            captureBlocked = null;
            RefreshBindings();
        };
        var bind = Button("Click to bind", BeginCapture);
        bind.IsPrimary = true;
        actionSearch.TextChanged += (_, _) => FilterActions();
        var editor = Module("Cockpit bindings", actionSearch, Row(Stack("Cockpit actions", actions), Stack("Assigned inputs", bindings)),
            Row(bind, Button("Edit / calibrate…", EditBinding), Button("Remove binding", RemoveBinding)), bindingStatus);
        editor.ClientSizeChanged += (_, _) =>
        {
            int available = editor.ClientSize.Width - editor.Padding.Horizontal - CockpitTheme.Logical(editor, 35);
            actions.Width = Math.Max(CockpitTheme.Logical(editor, 135), (int)(available * 0.51));
            bindings.Width = Math.Max(CockpitTheme.Logical(editor, 125), available - actions.Width);
        };
        var live = Module("Live input", deviceList, axisMeters, inputPreview, reportPreview);
        live.ClientSizeChanged += (_, _) => axisMeters.Width = Math.Max(1, live.ClientSize.Width - live.Padding.Horizontal -
            CockpitTheme.Logical(live, 10));
        panel.Controls.Add(Split(62, editor, live));
        FilterActions();
        RefreshProfileChoices();
        profileChoice.SelectedIndexChanged += (_, _) =>
        {
            if (!selectingProfile && profileChoice.SelectedItem is ProfileChoice choice)
                Run(() => SaveProfiles(profiles.Select(choice.Value.Id)));
        };
        mapper = new(profile);
        previewTimer.Tick += (_, _) => Poll();
        tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdatePreviewState();
            RefreshUpdates();
        };
    }

    private void FilterActions()
    {
        var selected = SelectedAction?.Id;
        var query = actionSearch.Text.Trim();
        actions.BeginUpdate();
        try
        {
            actions.Nodes.Clear();
            TreeNode? first = null, restore = null;
            foreach (var group in Cockpit.Actions.GroupBy(Group))
            {
                var matching = group.Where(action => query.Length == 0 ||
                    group.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    action.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    action.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 0)
                    continue;
                var section = actions.Nodes.Add(group.Key);
                foreach (var action in matching)
                {
                    var node = section.Nodes.Add(action.Label);
                    node.Tag = action;
                    node.ToolTipText = $"{action.Label} · {action.Kind}";
                    first ??= node;
                    if (action.Id == selected)
                        restore = node;
                }
                section.Expand();
            }
            actions.SelectedNode = restore ?? first;
        }
        finally { actions.EndUpdate(); }
        RefreshBindings();

        static string Group(CockpitAction action) => action.Kind switch
        {
            ActionKind.Axis => "Sticks and aiming",
            ActionKind.Pedal => "Pedals",
            ActionKind.Gear => "Gear lever",
            ActionKind.Tuner => "Tuner",
            ActionKind.Switch => "Maintained switches",
            _ when action.Id is "hatch" or "ignition" or "start" or "eject" => "Start and safety",
            _ when action.Id is "main" or "fire" or "lock" or "main-select" or "sub-select" or "magazine" => "Weapons",
            _ when action.Id.StartsWith("comm", StringComparison.Ordinal) => "Communications",
            _ => "Monitors and functions"
        };
    }

    private static FlowLayoutPanel Stack(string caption, Control control)
    {
        var stack = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        stack.Controls.Add(Caption(caption));
        stack.Controls.Add(control);
        return stack;
    }

    private void UpdatePreviewState()
    {
        if (tabs.SelectedTab?.Text == "Controls" && child is null)
        {
            devices ??= createDevices();
            previewTimer.Start();
        }
        else
            StopPreview();
    }

    private void StopPreview()
    {
        previewTimer.Stop();
        devices?.Dispose();
        devices = null;
        latest = [];
        captureBlocked = null;
        mapper = new(profile);
        axisMeters.Reset();
    }

    private void BeginCapture()
    {
        if (SelectedAction is null || devices is null)
            return;
        captureBlocked = latest.Where(sample => Math.Abs(sample.Value) > 0.2)
            .Select(sample => (sample.Device, sample.Control)).ToHashSet();
        // Ignore the button used to arm capture until it is released.
        captureBlocked.Add(("mouse", "Left"));
        bindingStatus.Text = "Release held controls, then press a button or move the desired axis. Esc cancels.";
    }

    private void Poll()
    {
        if (devices is null || mapper is null)
            return;
        var frame = devices.Poll(ContainsFocus);
        latest = frame.Samples;
        deviceList.Text = string.Join("\n", devices.Descriptions);
        inputPreview.Text = string.Join("   ", latest.Where(sample => Math.Abs(sample.Value) > 0.15).Take(5)
            .Select(sample => $"{sample.Device} {sample.Control} {sample.Value:0.00}"));
        var report = mapper.Map(frame);
        axisMeters.UpdateReport(report);
        reportPreview.Text = $"XID preview\n{Convert.ToHexString(report.AsSpan(0, 13))}\n{Convert.ToHexString(report.AsSpan(13))}";
        if (SelectedAction is { } selected)
            reportPreview.Text += $"\n{selected.Label}   {PreviewValue(selected, report)}";
        if (captureBlocked is null || SelectedAction is not { } action)
            return;
        if (!ContainsFocus || latest.Any(sample => sample.Device == "key" && sample.Control == "Escape"))
        {
            captureBlocked = null;
            RefreshBindings();
            return;
        }
        captureBlocked.RemoveWhere(key => !latest.Any(sample =>
            (sample.Device, sample.Control) == key && Math.Abs(sample.Value) > 0.2));
        var input = latest.Where(sample => Math.Abs(sample.Value) > (sample.Axis ? 0.65 : 0.5)
            && !captureBlocked.Contains((sample.Device, sample.Control)))
            .OrderBy(sample => sample.Device == "mouse" && sample.Axis).FirstOrDefault();
        if (input is null)
            return;
        captureBlocked = null;
        var direction = input.Axis && action.Kind != ActionKind.Axis ? Math.Sign(input.Value) : 0;
        var binding = new Binding(action.Id, input.Device, input.Control, Direction: direction,
            DeadZone: input.Axis ? 0.08 : 0);
        Run(() => SetProfile(profile with { Bindings = [.. profile.Bindings, binding] }));
    }

    private static string PreviewValue(CockpitAction action, byte[] report) => action.Kind switch
    {
        ActionKind.Switch or ActionKind.Button => (report[action.Offset] & action.Value) != 0 ? "On" : "Off",
        ActionKind.Gear => unchecked((sbyte)report[25]) == action.Value ? "Selected" : "—",
        ActionKind.Tuner => report[24] == action.Value ? "Selected" : "—",
        ActionKind.Axis when action.Offset >= 12 =>
            System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(action.Offset)).ToString(),
        _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(action.Offset)).ToString()
    };

    private void SetProfile(InputProfile next) => SaveProfiles(profiles.Replace(next));

    private void SaveProfiles(ProfileLibrary next)
    {
        if (profileError is not null && File.Exists(profileCatalog))
            File.Copy(profileCatalog, profileCatalog + "." + Guid.NewGuid().ToString("N") + ".backup");
        Storage.Write(profileCatalog, next);
        profiles = next;
        profile = profiles.Active.Profile;
        profileError = null;
        captureBlocked = null;
        RefreshProfileChoices();
        mapper = new(profile);
        RefreshBindings();
        RefreshSetup();
    }

    private void RefreshProfileChoices()
    {
        selectingProfile = true;
        try
        {
            profileChoice.Items.Clear();
            foreach (var entry in profiles.Entries)
            {
                int index = profileChoice.Items.Add(new ProfileChoice(entry));
                if (entry.Id == profiles.Selected)
                    profileChoice.SelectedIndex = index;
            }
            profileName.Text = profile.Name;
        }
        finally { selectingProfile = false; }
    }

    private sealed record ProfileChoice(NamedProfile Value)
    {
        public override string ToString() => Value.Profile.Name;
    }

    private void RefreshBindings()
    {
        bindings.Items.Clear();
        if (SelectedAction is { } action)
            foreach (var binding in profile.Bindings.Where(binding => binding.Action == action.Id))
                bindings.Items.Add(new BindingItem(binding));
        if (bindings.Items.Count > 0)
            bindings.SelectedIndex = 0;
        var conflicts = profile.Conflicts();
        bindingStatus.Text = conflicts.Length == 0 ? "No binding conflicts."
            : "Conflicting sources — " + string.Join(", ", conflicts);
        bindingStatus.ForeColor = conflicts.Length == 0 ? CockpitTheme.Ready : CockpitTheme.Alert;
    }

    private sealed record BindingItem(Binding Value)
    {
        public override string ToString() =>
            $"{Value.Device}  •  {Value.Control}" +
            (Value.Direction == 0 ? "" : Value.Direction < 0 ? " −" : " +") +
            (Value.Invert ? "  •  inverted" : "") + (Value.Maintained ? "  •  maintained" : "");
    }

    private void RemoveBinding()
    {
        if (bindings.SelectedItem is BindingItem item)
        {
            var list = profile.Bindings.ToList();
            list.Remove(item.Value);
            SetProfile(profile with { Bindings = list.ToArray() });
        }
    }

    private void EditBinding()
    {
        if (bindings.SelectedItem is not BindingItem item || SelectedAction is not { } action)
            return;
        var current = item.Value;
        using var dialog = new Form
        {
            Text = $"Binding for {action.Label}", ClientSize = new(385, 340), Font = Font,
            BackColor = CockpitTheme.Background, ForeColor = CockpitTheme.Text,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false
        };
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new(12), WrapContents = false
        };
        var invert = new CheckBox { Text = "Invert axis", Checked = current.Invert, AutoSize = true };
        var maintained = new CheckBox
        {
            Text = "Physical maintained position", Checked = current.Maintained, AutoSize = true,
            Enabled = action.Kind is ActionKind.Switch or ActionKind.Gear or ActionKind.Tuner
        };
        var direction = Number(current.Direction, -1, 1);
        var minimum = Number((decimal)current.Minimum, -1, 1, 2);
        var center = Number((decimal)current.Center, -1, 1, 2);
        var maximum = Number((decimal)current.Maximum, -1, 1, 2);
        var deadzone = Number((decimal)current.DeadZone, 0, 0.99m, 2);
        layout.Controls.AddRange([Caption(item.ToString()), invert, maintained,
            Row(Caption("Direction (−1, 0 or +1)"), direction), Row(Caption("Dead zone"), deadzone),
            Row(Caption("Calibration minimum"), minimum), Row(Caption("Calibration center"), center),
            Row(Caption("Calibration maximum"), maximum)]);
        var save = new CockpitButton { Text = "Save binding", DialogResult = DialogResult.OK, IsPrimary = true };
        var cancel = new CockpitButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Row(save, cancel));
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(layout);
        CockpitTheme.Skin(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var list = profile.Bindings.ToList();
        list[list.IndexOf(current)] = current with
        {
            Direction = (int)direction.Value, Invert = invert.Checked, Maintained = maintained.Checked,
            Minimum = (double)minimum.Value, Center = (double)center.Value,
            Maximum = (double)maximum.Value, DeadZone = (double)deadzone.Value
        };
        SetProfile(profile with { Bindings = list.ToArray() });
    }

    private void ImportProfile()
    {
        using var picker = new OpenFileDialog { Filter = "Quasar input profiles (*.json)|*.json" };
        if (picker.ShowDialog(this) == DialogResult.OK)
            SaveProfiles(profiles.Add(InputProfile.Load(picker.FileName)));
    }

    private void ExportProfile()
    {
        using var picker = new SaveFileDialog { Filter = "Quasar input profiles (*.json)|*.json", DefaultExt = "json" };
        if (picker.ShowDialog(this) == DialogResult.OK)
            Storage.Write(picker.FileName, profile.Validate());
    }

    private static NumericUpDown Number(decimal value, decimal min, decimal max, int decimals = 0) => new()
    {
        Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals,
        Increment = decimals > 0 ? 0.01m : 1, Width = 90
    };
}
