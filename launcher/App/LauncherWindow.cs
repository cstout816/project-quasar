using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Quasar.Launcher;

internal sealed partial class LauncherWindow : Form
{
    private readonly string userRoot, settingsFile, profileFile, profileCatalog;
    private readonly Installation? installation;
    private readonly Func<IInputSource> createDevices;
    private readonly Version version = RuntimeManifest.Installed(typeof(LauncherWindow).Assembly);
    private readonly CockpitPages tabs = new();
    private readonly Label summary = Caption(), issues = Caption(), runtimeSource = Caption(), dataPath = Caption();
    private readonly FlowLayoutPanel setupActions = Row();
    private readonly CockpitButton play = new() { Text = "Launch game", IsPrimary = true };
    private readonly CockpitButton selectImage = new() { Text = "Select image…" };
    private readonly Label status = new()
    {
        Dock = DockStyle.Bottom, Height = 34, Padding = new(24, 0, 24, 0),
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        BackColor = CockpitTheme.Field, ForeColor = CockpitTheme.Muted, AccessibleName = "Launcher status"
    };
    private readonly CancellationTokenSource closing = new();
    private Settings settings = new();
    private InputProfile profile = InputProfile.Keyboard();
    private ProfileLibrary profiles = ProfileLibrary.Empty(InputProfile.Keyboard());
    private RuntimeManifest? runtime;
    private string? manifestPath;
    private Process? child;
    private string? settingsError, profileError;
    private bool refreshing;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int? FixtureDpi { get; set; }

    public LauncherWindow(string userRoot, Installation? installation, Func<IInputSource>? createDevices = null)
    {
        SuspendLayout();
        this.userRoot = userRoot;
        this.installation = installation;
        this.createDevices = createDevices ?? (() => new Devices());
        settingsFile = Path.Combine(userRoot, "settings.json");
        profileFile = Path.Combine(userRoot, "profiles", "active.json");
        profileCatalog = Path.Combine(userRoot, "profiles", "library.json");
        Text = "Project Quasar";
        Icon = QuasarMark.LoadIcon();
        Font = new("Segoe UI", 10);
        BackColor = CockpitTheme.Background;
        ForeColor = CockpitTheme.Text;
        ClientSize = new(1080, 735);
        MinimumSize = new(700, 460);
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(tabs);
        Controls.Add(status);
        Controls.Add(new CockpitHeader());
        HandleCreated += (_, _) => CockpitTheme.StyleWindowFrame(Handle);
        status.Text = $"Player data   •   {userRoot}";
        LoadSettings();
        BuildPlay();
        BuildControls();
        BuildGraphics();
        BuildUpdates();
        CockpitTheme.Skin(this);
        RefreshSetup();
        AutoScaleDimensions = new(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
        Shown += async (_, _) =>
        {
            var workArea = Screen.FromControl(this).WorkingArea;
            if (Width > workArea.Width || Height > workArea.Height)
                Size = new(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
            if (settings.AutomaticChecks && installUpdates.Enabled)
                await CheckUpdates();
        };
        FormClosing += (_, e) =>
        {
            if (child is { HasExited: false } || updateBusy)
            {
                e.Cancel = true;
                Notify(child is { HasExited: false } ? "Close the runtime before closing the launcher." :
                    "Wait for the update check or installation to finish.");
            }
        };
        FormClosed += (_, _) =>
        {
            closing.Cancel();
            previewTimer.Dispose();
            devices?.Dispose();
            child?.Dispose();
            closing.Dispose();
        };
    }

    private void LoadSettings()
    {
        try { settings = Settings.Load(settingsFile); }
        catch (Exception error) when (IsUserError(error))
        {
            settingsError = $"Settings could not be read. Use Reset launcher settings to continue. {error.Message}";
        }

        try
        {
            profiles = ProfileLibrary.Load(profileCatalog, profileFile);
            profile = profiles.Active.Profile;
        }
        catch (Exception error) when (IsUserError(error))
        {
            profileError = $"The active profile could not be read. Import a profile or choose a default. {error.Message}";
        }
    }

    private void BuildPlay()
    {
        var panel = Page("Play");
        summary.Font = new("Consolas", 10);
        var launchCard = Module("Steel Battalion", summary, issues, setupActions,
            Row(play, Button("Recheck setup", RefreshSetup)));
        panel.Controls.Add(Split(59, launchCard, new TacticalDisplay()));
        selectImage.Click += (_, _) => Run(PickImage);
        var gameData = Module("Game data",
            Caption("Select your own legally obtained Steel Battalion image or extracted game directory."),
            dataPath, Row(selectImage, Button("Select folder…", PickFolder)));
        var runtimeSettings = Module("Runtime", runtimeSource,
            Row(Button("Local runtime…", PickRuntime),
                Button("Use installed runtime", () => ChangeSettings(settings with { DevelopmentManifest = "" }))));
        panel.Controls.Add(Split(50, gameData, runtimeSettings));
        panel.Controls.Add(Row(Button("Reset launcher settings…", Reset)));
        play.Click += async (_, _) => await Launch();
    }

    private void RefreshSetup()
    {
        var blockers = new List<string>();
        var actions = new Dictionary<string, Action>();
        void Block(string message, string label, Action action)
        {
            blockers.Add(message);
            actions.TryAdd(label, action);
        }
        if (settingsError is not null)
            Block(settingsError, "Reset launcher settings…", Reset);
        runtime = null;
        manifestPath = null;
        var useLocal = !string.IsNullOrEmpty(settings.DevelopmentManifest);
        try
        {
            var pair = installation?.RunningPair;
            manifestPath = useLocal ? settings.DevelopmentManifest : pair is null ? null : installation!.RuntimePath(pair);
            if (manifestPath is null)
                throw new InvalidDataException("Install a supported runtime or select a local development manifest.");
            var selected = RuntimeManifest.Load(manifestPath, version);
            if (!useLocal && pair?.MinimumRuntime is string min &&
                !RuntimeManifest.InRange(Version.Parse(selected.Version), min, pair.MaximumRuntimeExclusive!))
                throw new InvalidDataException("The installed runtime is outside the launcher compatibility range.");
            runtime = selected;
        }
        catch (Exception error) when (IsUserError(error))
        {
            Block(error.Message, "Choose local runtime…", PickRuntime);
            if (installation is not null && publicKey is not null)
                actions.TryAdd("Manage runtime in Updates", () => tabs.SelectedIndex = 3);
        }

        try
        {
            if (runtime is not null && manifestPath is not null)
                RuntimeLaunch.Prepare(manifestPath, runtime, settings.GameData, userRoot, profileFile,
                    settings.Graphics ?? Graphics.Original, ApplicationRoots());
            else
                GameData.Validate(settings.GameData);
        }
        catch (Exception error) when (IsUserError(error))
        {
            Block($"Game setup: {error.Message}", "Choose game data", () =>
            {
                selectImage.Focus();
                ((FlowLayoutPanel)tabs.TabPages[0]).ScrollControlIntoView(selectImage);
            });
        }

        if (runtime?.Supports("input-profile-v1") == true)
        {
            if (profileError is not null) Block(profileError, "Open Controls", () => tabs.SelectedIndex = 1);
            if (profile.Conflicts().Length > 0) Block("The active profile has conflicting bindings.", "Open Controls", () => tabs.SelectedIndex = 1);
        }
        if (child is { HasExited: false } || RuntimeLease.IsRunning(userRoot))
            Block("The runtime is running. Close its window before launching again.", "Recheck runtime", RefreshSetup);
        if (applyingUpdate)
            Block("Wait for the update to finish staging before launching.", "View update progress", () => tabs.SelectedIndex = 3);
        summary.Text = $"Launcher {version}  /  Runtime {runtime?.Version ?? "unavailable"}\nProfile  {profile.Name}";
        dataPath.Text = string.IsNullOrWhiteSpace(settings.GameData) ? "Game data has not been selected." : settings.GameData;
        var capabilities = runtime?.Capabilities.Length > 0 ? string.Join(", ", runtime.Capabilities) : "None";
        runtimeSource.Text = (useLocal ? "Local development" : "Installed runtime") +
            (manifestPath is null ? "" : $" — {manifestPath}") + $"\nDeclared capabilities: {capabilities}";
        issues.Text = blockers.Count == 0 ? "Ready to play." : string.Join("\n", blockers.Distinct().Select(issue => "• " + issue));
        issues.ForeColor = blockers.Count == 0 ? CockpitTheme.Ready : CockpitTheme.Alert;
        play.Enabled = blockers.Count == 0;
        if (!setupActions.Controls.Cast<Control>().Select(control => control.Text).SequenceEqual(actions.Keys))
        {
            setupActions.SuspendLayout();
            foreach (Control control in setupActions.Controls.Cast<Control>().ToArray())
                control.Dispose();
            foreach (var (label, action) in actions)
            {
                var button = Button(label, action);
                setupActions.Controls.Add(button);
                button.ApplyStyle();
            }
            setupActions.ResumeLayout();
        }
        setupActions.Visible = actions.Count > 0;
        RefreshGraphics();
        RefreshUpdates();
        profileUse.Text = runtime?.Supports("input-profile-v1") == true
            ? "The selected runtime declares support for this profile."
            : "Launcher preview only until a compatible runtime consumes input-profile-v1.";
    }

    private string[] ApplicationRoots() => installation is null ? [AppContext.BaseDirectory] : [installation.Root];

    private async Task Launch()
    {
        RefreshSetup();
        if (!play.Enabled || runtime is null || manifestPath is null)
            return;
        try
        {
            var info = RuntimeLaunch.Prepare(manifestPath, runtime, settings.GameData, userRoot, profileFile,
                settings.Graphics ?? Graphics.Original, ApplicationRoots());
            Directory.CreateDirectory(Path.Combine(userRoot, "Saves"));
            if (runtime.Supports("input-profile-v1"))
                Storage.Write(profileFile, profile.Validate());
            StopPreview();
            using var started = Process.Start(info) ?? throw new IOException("Could not start the runtime.");
            child = started;
            try
            {
                if (!started.HasExited)
                    RuntimeLease.Save(userRoot, started);
            }
            catch (Exception error) when (IsUserError(error))
            {
                Notify($"Could not record runtime process: {error.Message}");
                await started.WaitForExitAsync();
                return;
            }
            RefreshSetup();
            Notify("Runtime started.");
            await started.WaitForExitAsync();
            Notify($"Runtime exited with code {started.ExitCode}.");
        }
        catch (Exception error) when (IsUserError(error)) { Notify(error.Message); }
        finally
        {
            child = null;
            UpdatePreviewState();
            RefreshSetup();
        }
    }

    private void PickImage()
    {
        using var picker = new OpenFileDialog { Filter = "Xbox disc images (*.iso;*.xiso)|*.iso;*.xiso" };
        if (picker.ShowDialog(this) == DialogResult.OK)
            SelectData(picker.FileName);
    }

    private void PickFolder()
    {
        using var picker = new FolderBrowserDialog { Description = "Select the extracted game directory.", UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) == DialogResult.OK)
            SelectData(picker.SelectedPath);
    }

    private void SelectData(string path) => Run(() =>
    {
        var format = GameData.Validate(path);
        ChangeSettings(settings with { GameData = path });
        Notify($"Validated Steel Battalion title and {format} layout.");
    });

    private void PickRuntime()
    {
        using var picker = new OpenFileDialog { Filter = "Quasar runtime manifest (*.json)|*.json" };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        Run(() =>
        {
            RuntimeManifest.Load(picker.FileName, version);
            if (MessageBox.Show(this, "A local development runtime runs with your Windows account. Select only a build you trust.",
                "Use this development runtime?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                ChangeSettings(settings with { DevelopmentManifest = picker.FileName });
        });
    }

    private void Reset()
    {
        if (MessageBox.Show(this, "Reset the game-data and runtime selections, graphics preferences and update preferences?",
            "Reset launcher settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        if (File.Exists(settingsFile))
            File.Copy(settingsFile, settingsFile + "." + Guid.NewGuid().ToString("N") + ".backup");
        SaveSettings(new());
        Notify("Launcher settings reset.");
    }

    private void ChangeSettings(Settings next)
    {
        if (settingsError is not null)
        {
            Notify("Reset launcher settings before saving setup changes.");
            return;
        }
        SaveSettings(next);
    }

    private void SaveSettings(Settings next)
    {
        Storage.Write(settingsFile, next);
        settings = next;
        settingsError = null;
        RefreshSetup();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (IsUserError(error)) { Notify(error.Message); }
    }

    private static bool IsUserError(Exception e) =>
        e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException
        or KeyNotFoundException or Win32Exception or BadImageFormatException or System.Security.Cryptography.CryptographicException
        or System.Net.Http.HttpRequestException or OperationCanceledException;

    private void Notify(string message) => status.Text = message;

    private FlowLayoutPanel Page(string name)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new(19, 15, 19, 12), BackColor = CockpitTheme.Background
        };
        panel.Layout += (_, _) =>
        {
            int width = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal -
                CockpitTheme.Logical(panel, 18));
            foreach (var child in panel.Controls.Cast<Control>().Where(child => child is ModulePanel or ResponsiveSplit))
            {
                if (child is ResponsiveSplit split)
                    split.Arrange(width);
                child.MinimumSize = new(width, 0);
                child.MaximumSize = new(width, 0);
                FitLabels(child);
            }
        };
        tabs.Add(name, panel);
        return panel;
    }

    private static Label Caption(string text = "") => new()
    {
        Text = text, AutoSize = true, MaximumSize = new(760, 0), Margin = new(3, 4, 3, 4),
        ForeColor = CockpitTheme.Muted, UseMnemonic = false
    };

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new("Segoe UI Semibold", 14),
        ForeColor = CockpitTheme.Text, Margin = new(3, 0, 3, 6), UseMnemonic = false
    };

    private CockpitButton Button(string text, Action action)
    {
        var button = new CockpitButton { Text = text, AutoSize = true };
        button.Click += (_, _) => Run(action);
        return button;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static ModulePanel Module(string title, params Control[] controls)
    {
        var module = new ModulePanel();
        module.Controls.Add(Heading(title));
        module.Controls.AddRange(controls);
        module.ClientSizeChanged += (_, _) => FitLabels(module);
        return module;
    }

    private static ResponsiveSplit Split(float leftShare, Control left, Control right) => new(leftShare, left, right);

    private static void FitLabels(Control container)
    {
        if (container.Width <= container.Padding.Horizontal + 20)
            return;
        foreach (var control in container.Controls.Cast<Control>().Where(control => control is Label or FlowLayoutPanel))
            control.MaximumSize = new(Math.Max(1, container.Width - container.Padding.Horizontal - control.Margin.Horizontal - 4), 0);
    }
}
