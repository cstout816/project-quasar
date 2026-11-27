using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Quasar;
using Quasar.Launcher;
using Binding = Quasar.Binding;
using Graphics = Quasar.Graphics;

if (args.Length < 3 || args.Length % 2 != 1)
    throw new ArgumentException("Pass the baseline fixture, upgraded fixture, and bootstrap build directories.");

var temp = Directory.CreateTempSubdirectory("quasar-fixtures-").FullName;
var options = new Dictionary<string, string>();
for (int index = 3; index < args.Length; index += 2)
    options.Add(args[index], args[index + 1]);
try
{
    SettingsTest();
    ControlsTest();
    await LaunchTest();
    await UpdatesTest();
    await RuntimePatchesTest();
    if (options.TryGetValue("--portable", out var package))
        await PackagedSmoke.Run(package, args[0], temp, Xbe(0x43430002));
    Console.WriteLine("All launcher fixture groups passed.");
}
finally { Directory.Delete(temp, recursive: true); }

void SettingsTest()
{
    var path = Path.Combine(temp, "settings.json");
    File.WriteAllText(path, """{"schema":1,"gameData":"old game path","runtimeManifest":"old runtime path"}""");
    var migrated = Settings.Load(path);
    Require(migrated.Schema == 2 && migrated.GameData == "old game path" &&
        migrated.DevelopmentManifest == "old runtime path" && !migrated.AutomaticChecks);
    var updated = migrated with { Graphics = new(VSync: true, Experimental: true) };
    Storage.Write(path, updated);
    Require(Settings.Load(path) == updated && !Directory.EnumerateFiles(temp, "*.tmp").Any());
    File.WriteAllText(path, """{"schema":99}""");
    Reject<InvalidDataException>(() => Settings.Load(path));
    var legacy = Path.Combine(temp, "legacy-profile.json");
    var libraryPath = Path.Combine(temp, "profile-library.json");
    Storage.Write(legacy, InputProfile.Gamepad("xinput:2") with { Name = "Pilot one" });
    var library = ProfileLibrary.Load(libraryPath, legacy);
    var original = library.Selected;
    library = library.Add(InputProfile.Keyboard() with { Name = "Pilot one" });
    Require(library.Active.Profile.Name == "Pilot one (2)");
    Storage.Write(libraryPath, library.Select(original));
    var reopened = ProfileLibrary.Load(libraryPath, legacy);
    Require(reopened.Entries.Length == 2 && reopened.Active.Profile.Name == "Pilot one" &&
        reopened.Active.Profile.Bindings.Any(binding => binding.Device == "xinput:2"));
    Console.WriteLine("PASS settings migration, replacement, and unsupported schema");
}

void ControlsTest()
{
    var bindings = new Binding[]
    {
        new("hatch", "pad", "A"), new("filter", "pad", "B"), new("filter", "keys", "F"),
        new("gear-r", "stick", "Reverse", Maintained: true),
        new("gear-3", "pad", "Right"), new("tuner-11", "keys", "Digit"),
        new("aim-x", "stick", "X", Invert: true, DeadZone: .1, Minimum: -.8, Center: .1, Maximum: .8),
        new("brake", "pedals", "Brake", DeadZone: .1),
        new("accelerator", "pad", "RT")
    };
    var profile = new InputProfile(1, "Mixed", bindings).Validate();
    var path = Path.Combine(temp, "profile.json");
    Storage.Write(path, profile);
    Require(InputProfile.Load(path).Bindings.SequenceEqual(bindings));
    Require(profile.Conflicts().Length == 0);
    Require((profile with { Bindings = [.. bindings, new("ignition", "pad", "A")] }).Conflicts().Length == 1);
    Require(InputProfile.Keyboard().Validate().Conflicts().Length == 0);
    Reject<InvalidDataException>(() => (profile with { Bindings = [new("brake", "x", "y", DeadZone: 1)] }).Validate());

    var map = new XidMapper(profile);
    var all = new HashSet<string> { "pad", "keys", "stick", "pedals" };
    byte[] Report(params InputSample[] samples) => map.Map(new(all, samples));
    Require(Report()[25] == 255);
    var report = Report(new("pad", "A", 1), new("pad", "B", 1), new("stick", "X", .8, true),
        new("stick", "Reverse", 1), new("keys", "Digit", 1), new("pedals", "Brake", .5, true, true),
        new("pad", "RT", 1, true, true));
    Require(report[2] == 0x10 && report[6] == 4 && report[25] == 254 && report[24] == 11);
    Require(report[8] == 0 && report[9] == 0 && report[21] is >= 112 and <= 114 && report[23] == 255);
    report = Report(new InputSample("pad", "B", 1));
    Require(report[2] == 0 && report[6] == 4 && report[25] == 0);
    report = Report(new("keys", "F", 1), new("pad", "Right", 1));
    Require(report[6] == 0 && report[25] == 3);
    all.Remove("pad");
    report = Report(new InputSample("pad", "A", 1));
    Require(report[2] == 0 && report[25] == 255 && report[23] == 0);
    all.Add("pad");
    Require(Report(new("pad", "Right", 1), new("pad", "B", 1))[6] == 0);
    Report();
    Require(Report(new InputSample("pad", "B", 1))[6] == 4);
    all.Remove("pad");
    Require(Report()[6] == 0);

    byte[] physical = Convert.FromHexString("001A2008000040003412CDEF00000100FFFF000000FFFF7F0CFE");
    var original = map.Map(new(all, [new("keys", "F", 1)], physical));
    Require(original.SequenceEqual(physical) && !ReferenceEquals(original, physical));
    report = Report();
    Require(report[2] == 0 && report[6] == 0 && report[25] == 255 && report[9] == 128);
    physical[1] = 0;
    Require(!XidMapper.IsOriginalReport(physical));
    Console.WriteLine("PASS profile round trip, conflicts, axis calibration, XID fields, device priority and disconnect");
}

async Task LaunchTest()
{
    var root = Path.Combine(temp, "runtime & fixture");
    FileCopy(args[0], root);
    Directory.CreateDirectory(Path.Combine(root, "work dir"));
    var manifest = Path.Combine(root, "quasar-runtime.json");
    var runtime = new RuntimeManifest(1, "1.2.0", "win-x64", "Quasar.Launcher.exe", "work dir",
        ["extracted", "xdvdfs"], ["input-profile-v1", "original-640x480", "fullscreen", "vsync"], "0.1.0", "1.0.0");
    Storage.Write(manifest, runtime);
    var version = new Version(0, 1, 0);
    RuntimeManifest.Load(manifest, version);
    var data = Path.Combine(temp, "game & test (quasar) 漢字");
    Directory.CreateDirectory(data);
    var xbe = Xbe(0x43430002);
    File.WriteAllBytes(Path.Combine(data, "default.xbe"), xbe);
    Require(GameData.Validate(data) == "extracted");
    var image = Path.Combine(temp, "fixture image.xiso");
    WriteImage(image, xbe);
    Require(GameData.Validate(image) == "xdvdfs");
    WriteImage(image, Xbe(1));
    Reject<InvalidDataException>(() => GameData.Validate(image));
    var user = Path.Combine(temp, "player data");
    var profile = Path.Combine(user, "a & b.json");
    Storage.Write(profile, InputProfile.Keyboard());
    Environment.SetEnvironmentVariable("RECOMP_FIXTURE", "must not leak");
    var start = RuntimeLaunch.Prepare(manifest, runtime, data + "\\", user, profile,
        new("fullscreen", 1920, 1080, "16:9", true, 72, true));
    using var stub = Process.Start(start)!;
    await stub.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Require(stub.ExitCode == 0);
    var proof = Storage.Read<LaunchProof>(Path.Combine(start.WorkingDirectory, "launch-proof.json"));
    Require(proof.WorkingDirectory == start.WorkingDirectory && proof.InheritedDiagnostic is null);
    Require(proof.Arguments.Take(10).SequenceEqual(new[] { "--quasar-contract", "1", "--game-data", data + "\\",
        "--game-data-format", "extracted", "--save-dir", Path.Combine(user, "Saves"), "--input-profile", profile }));
    var settings = JsonSerializer.Deserialize<Graphics>(proof.Arguments[11], Storage.Json);
    Require(proof.Arguments[10] == "--graphics-json" &&
        settings == new Graphics("fullscreen", 640, 480, "4:3", true, 0, true));
    Reject<InvalidDataException>(() => RuntimeLaunch.Prepare(manifest, runtime, data, user, profile, new(), temp));
    Reject<InvalidDataException>(() => RuntimeManifest.Load(manifest, new Version(1, 0, 0)));
    Reject<InvalidDataException>(() => (runtime with { Executable = "../escape.exe" }).Resolve(manifest));
    UiTest(user, data, manifest, options.GetValueOrDefault("--preview"));
    Console.WriteLine("PASS title and image validation, capabilities and native process argument quoting");
}

static void UiTest(string user, string data, string manifest, string? previews)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.EnableVisualStyles();
            var file = Path.Combine(user, "settings.json");
            using (var unconfigured = Open())
            {
                var tabs = Find<CockpitPages>(unconfigured).Single();
                Require(tabs.TabPages.Select(tab => tab.Text)
                    .SequenceEqual(new[] { "Play", "Controls", "Graphics", "Updates" }));
                Require(!Find<Button>(tabs.TabPages[0]).Single(button => button.Text == "Launch game").Enabled);
                Require(Find<Button>(tabs.TabPages[0]).Any(button => button.Text == "Choose game data"));
                unconfigured.Close();
            }
            Storage.Write(file, new Settings(GameData: data, DevelopmentManifest: manifest,
                Graphics: new(Experimental: true)));
            using var configured = Open();
            var configuredTabs = Find<CockpitPages>(configured).Single();
            Require(Find<Button>(configuredTabs.TabPages[0]).Single(button => button.Text == "Launch game").Enabled);
            Find<CockpitButton>(configuredTabs).Single(button => button.IsNavigation && button.Text == "Controls").PerformClick();
            Require(configuredTabs.SelectedIndex == 1);
            var controls = configuredTabs.TabPages[1];
            var search = Find<TextBox>(controls).Single(control => control.AccessibleName == "Search cockpit actions");
            search.Text = "Brake";
            var actions = Find<TreeView>(controls).Single();
            Require(actions.Nodes.Count == 1 && actions.Nodes[0].Text == "Pedals" && actions.SelectedNode?.Text == "Brake pedal");
            search.Clear();
            Find<Button>(controls).Single(button => button.Text == "Xbox defaults").PerformClick();
            var profiles = Find<ComboBox>(controls).Single();
            Require(profiles.Items.Count == 2 && profiles.Text == "Xbox gamepad");
            typeof(LauncherWindow).GetMethod("Poll",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(configured, null);
            Require(Find<AxisMeters>(controls).Single().AccessibleDescription!.Contains("Aim X 34%", StringComparison.Ordinal));
            profiles.SelectedIndex = 0;
            Require(profiles.Text == "Keyboard and mouse");
            var keyboard = typeof(CockpitPages).GetMethod("ProcessCmdKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Require((bool)keyboard.Invoke(configuredTabs, [new Message(), Keys.Control | Keys.Tab])! && configuredTabs.SelectedIndex == 2);
            Require((bool)keyboard.Invoke(configuredTabs, [new Message(), Keys.Control | Keys.Shift | Keys.Tab])! && configuredTabs.SelectedIndex == 1);
            configuredTabs.SelectedIndex = 2;
            var graphics = configuredTabs.TabPages[2];
            Require(Find<ComboBox>(graphics).Single().Items.Cast<string>()
                .SequenceEqual(new[] { "windowed", "fullscreen" }));
            Require(Find<CheckBox>(graphics).Single(control => control.Text == "VSync").Enabled);
            Require(Find<NumericUpDown>(graphics).All(control => !control.Enabled));
            configuredTabs.SelectedIndex = 3;
            Require(!Find<Button>(configuredTabs.TabPages[3]).Single(control => control.Text == "Check for updates").Enabled);
            configured.Close();

            foreach (int dpi in new[] { 96, 144, 192 })
            {
                using var scaled = Open();
                DpiFixture.Scale(scaled, dpi);
                var pages = Find<CockpitPages>(scaled).Single();
                var nav = Find<CockpitButton>(pages).Where(button => button.IsNavigation).ToArray();
                Require(nav.Select(button => button.Left).Distinct().Count() == 1);
                Require(nav.Zip(nav.Skip(1), (above, below) => above.Bottom <= below.Top).All(value => value));
                Require(nav.All(button => TextRenderer.MeasureText(button.Text, button.Font).Width < button.Width));
                Require(nav.All(button => button.Height <= 49 * dpi / 96));
                for (int view = 0; view < pages.TabPages.Count; view++)
                {
                    pages.SelectedIndex = view;
                    Application.DoEvents();
                    var page = (FlowLayoutPanel)pages.TabPages[view];
                    Require(!page.HorizontalScroll.Visible);
                    Require(page.Controls.Cast<Control>().Where(control => control.Visible)
                        .All(control => control.Right <= page.ClientSize.Width));
                    Capture(scaled, $"{page.Text.ToLowerInvariant()}-{dpi * 100 / 96}");
                }
                scaled.Close();
            }
        }
        catch (Exception error) { failure = error; }
    }) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Require(thread.Join(TimeSpan.FromSeconds(45)));
    if (failure is not null)
        throw new InvalidOperationException("Launcher window fixture failed.", failure);
    Console.WriteLine("PASS sidebar and keyboard navigation, setup actions, grouped search, named profiles and 100/150/200% layout");

    LauncherWindow Open()
    {
        var window = new LauncherWindow(user, installation: null, () => new FixtureInput()) { ShowInTaskbar = false, Opacity = 0 };
        window.Show();
        Application.DoEvents();
        return window;
    }

    void Capture(Form window, string name)
    {
        if (previews is null)
            return;
        Application.DoEvents();
        Directory.CreateDirectory(previews);
        using var image = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
        image.Save(Path.Combine(previews, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    static IEnumerable<T> Find<T>(Control node) where T : Control =>
        node.Controls.Cast<Control>().SelectMany(control =>
            (control is T result ? new[] { result } : Array.Empty<T>()).Concat(Find<T>(control)));
}

async Task UpdatesTest()
{
    var root = Path.Combine(temp, "install with spaces");
    FileCopy(args[2], root);
    FileCopy(args[0], Path.Combine(root, "bundled"));
    var install = new Installation(root);
    using var key = RSA.Create(3072);
    var publicKey = key.ExportSubjectPublicKeyInfoPem();
    var archive = Path.Combine(temp, "fixture-launcher.zip");
    var launcher = MakeArchive(archive, "launcher", "0.2.0", PackageFiles(args[1]));
    var release = new ReleaseManifest(1, "win-x64", 1, "1.0.0", "2.0.0", Launcher: launcher);
    var (manifest, signature) = Sign(release);
    Require(UpdateVerification.VerifyManifest(manifest, signature, publicKey) == release);
    Reject<InvalidOperationException>(() => UpdateVerification.VerifyManifest(manifest, signature, null));
    var corruptSignature = signature.ToArray();
    corruptSignature[5] ^= 1;
    Reject<InvalidDataException>(() => UpdateVerification.VerifyManifest(manifest, corruptSignature, publicKey));

    var wrongPlatform = Sign(release with { Platform = "other" });
    Reject<InvalidDataException>(() => UpdateVerification.VerifyManifest(wrongPlatform.Bytes, wrongPlatform.Signature, publicKey));
    var corruptZip = Path.Combine(temp, "corrupt.zip");
    var corrupted = File.ReadAllBytes(archive);
    corrupted[0] ^= 1;
    File.WriteAllBytes(corruptZip, corrupted);
    Reject<InvalidDataException>(() => install.Stage(manifest, signature, publicKey, corruptZip, null, new(0, 1, 0)));

    var traversalZip = Path.Combine(temp, "traversal.zip");
    var traversal = Sign(release with { Launcher = MakeArchive(traversalZip, "launcher", "0.2.0",
        [.. PackageFiles(args[1]), ("../outside.exe", new byte[] { 1 })]) });
    Reject<InvalidDataException>(() => install.Stage(traversal.Bytes, traversal.Signature, publicKey,
        traversalZip, null, new(0, 1, 0)));
    Require(!File.Exists(Path.Combine(root, "outside.exe")) && install.Read().Pending is null);

    var runtimeZip = Path.Combine(temp, "runtime.zip");
    var runtime = new RuntimeManifest(1, "1.4.0", "win-x64", "Quasar.Launcher.exe", ".",
        ["extracted"], [], "0.2.0", "0.3.0");
    var runtimeFiles = PackageFiles(args[0]).Append(("quasar-runtime.json", JsonSerializer.SerializeToUtf8Bytes(runtime, Storage.Json)));
    var runtimePackage = MakeArchive(runtimeZip, "runtime", runtime.Version, runtimeFiles);
    var incompatible = Sign(release with { Runtime = runtimePackage, MaximumRuntimeExclusive = "1.3.0" });
    Reject<InvalidDataException>(() => install.Stage(incompatible.Bytes, incompatible.Signature, publicKey, archive, runtimeZip, new(0, 1, 0)));
    Require(install.Read().Pending is null);

    var combination = Sign(release with { Runtime = runtimePackage });
    var sentinel = Path.Combine(temp, "preserved saves and settings.txt");
    File.WriteAllText(sentinel, "player data");
    var hold = Path.Combine(root, "hold-baseline-fixture");
    File.WriteAllText(hold, "hold the current launcher open");
    Slots first;
    using (var bootstrap = StartBootstrap(root))
    {
        try
        {
            var old = await WaitForObservation(root);
            using var oldProcess = Process.GetProcessById(old.Pid);
            Require(old is { Slot: "bundled", Version: "0.1.0" });
            first = install.Stage(combination.Bytes, combination.Signature, publicKey, archive, runtimeZip, new(0, 1, 0));
            Require(!oldProcess.HasExited && install.Read().Active.Launcher == "bundled" && install.Read().Pending == first);
        }
        finally
        {
            File.Delete(hold);
            await FinishBootstrap(bootstrap);
        }
    }
    Require(install.Read().Active == first && install.Read().Previous?.Launcher == "bundled" &&
        install.Read().Trial is null && File.ReadAllText(sentinel) == "player data");
    Require(Storage.Read<Observation>(Path.Combine(root, "observed.json")) is { Slot: var slot, Version: "0.2.0" }
        && slot == first.Launcher);

    var latestArchive = Path.Combine(temp, "latest.zip");
    var latest = Sign(release with { Launcher = MakeArchive(latestArchive, "launcher", "0.3.0", PackageFiles(args[0])) });
    Reject<InvalidDataException>(() => install.Stage(latest.Bytes, latest.Signature, publicKey, latestArchive, null, new(0, 2, 0)));

    var runtimeOnlyZip = Path.Combine(temp, "runtime-only.zip");
    var runtimeV2 = runtime with { Version = "1.5.0" };
    var runtimeOnly = Sign(release with { Launcher = null, Runtime = MakeArchive(runtimeOnlyZip, "runtime", "1.5.0",
        PackageFiles(args[0]).Append(("quasar-runtime.json", JsonSerializer.SerializeToUtf8Bytes(runtimeV2, Storage.Json)))) });
    var second = install.Stage(runtimeOnly.Bytes, runtimeOnly.Signature, publicKey, null, runtimeOnlyZip, new(0, 2, 0));
    Require(second.Launcher == first.Launcher && second.Runtime != first.Runtime);

    // Terminate the supervisor during a real pending-to-trial restart. The fixtures are the only children.
    File.WriteAllText(Path.Combine(root, "interrupt-fixture"), "pause before readiness");
    File.Delete(Path.Combine(root, "observed.json"));
    using (var bootstrap = StartBootstrap(root))
    {
        var child = await WaitForObservation(root);
        using var process = Process.GetProcessById(child.Pid);
        bootstrap.Kill();
        await bootstrap.WaitForExitAsync();
        process.Kill();
        await process.WaitForExitAsync();
    }
    Require(install.Read().Trial == second && install.Read().Active == first);
    await RunBootstrap(root);
    Require(install.Read().Active == first && install.Read().Trial is null && install.Read().Pending is null);
    install.QueueRollback();
    File.Delete(Path.Combine(root, "interrupt-fixture"));
    await RunBootstrap(root);
    Require(install.Read().Active.Launcher == "bundled" && install.Read().Active.Runtime is null &&
        install.Read().Previous == first && File.ReadAllText(sentinel) == "player data");
    Require(Storage.Read<Observation>(Path.Combine(root, "observed.json")).Version == "0.1.0");
    Console.WriteLine("PASS manifest trust, SHA-256, traversal, compatibility, process self-update, interrupted restart and rollback");

    (byte[] Bytes, byte[] Signature) Sign(ReleaseManifest value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Storage.Json);
        return (bytes, key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }
}

async Task RuntimePatchesTest()
{
    var install = new Installation(Path.Combine(temp, "patched-install"));
    FileCopy(args[2], install.Root);
    FileCopy(args[0], Path.Combine(install.Root, "bundled"));
    var baseline = Path.Combine(temp, "baseline-release");
    var target = Path.Combine(temp, "target-release");
    FileCopy(args[0], baseline);
    FileCopy(args[0], target);
    foreach (var directory in new[] { baseline, target })
        foreach (var path in Directory.GetFiles(directory, "*.pdb").Append(Path.Combine(directory, "Core.deps.json")))
            File.Delete(path);

    var before = new RuntimeManifest(1, "1.1.0", "win-x64", "Quasar.Launcher.exe", ".",
        ["extracted"], [], "0.1.0", "1.0.0");
    Storage.Write(Path.Combine(baseline, "quasar-runtime.json"), before);
    Storage.Write(Path.Combine(target, "quasar-runtime.json"), before with { Version = "1.2.0" });
    var random = new Random(703);
    var original = new byte[44 * 16 * 1024 + 17];
    var unchanged = new byte[128 * 1024];
    random.NextBytes(original);
    random.NextBytes(unchanged);
    var updated = new byte[original.Length + 16 * 1024];
    random.NextBytes(updated.AsSpan(0, 16 * 1024));
    original.CopyTo(updated, 16 * 1024);
    updated[50_000] ^= 0xa1;
    File.WriteAllBytes(Path.Combine(baseline, "Renderer.dll"), original);
    File.WriteAllBytes(Path.Combine(target, "Renderer.dll"), updated);
    File.WriteAllBytes(Path.Combine(baseline, "Common.dll"), unchanged);
    File.WriteAllBytes(Path.Combine(target, "Common.dll"), unchanged);
    File.WriteAllBytes(Path.Combine(baseline, "Retired.dll"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(target, "New.dll"), [4, 5, 6]);

    var baseZip = Path.Combine(temp, "base-runtime.zip");
    var targetZip = Path.Combine(temp, "target-runtime.zip");
    var basePackage = MakeArchive(baseZip, "runtime", "1.1.0", PackageFiles(baseline));
    var targetPackage = MakeArchive(targetZip, "runtime", "1.2.0", PackageFiles(target));
    var patchPath = Path.Combine(temp, RuntimePatch.AssetName(basePackage.Version, targetPackage.Version));
    var patch = RuntimePatch.Create(baseline, target, patchPath);
    Require(patch.Size < targetPackage.Size / 10);
    using var key = RSA.Create(3072);
    var publicKey = key.ExportSubjectPublicKeyInfoPem();
    var baseRelease = new ReleaseManifest(1, "win-x64", 1, "1.0.0", "2.0.0", Runtime: basePackage);
    var baseManifest = Serialize(baseRelease);
    install.Stage(baseManifest, Sign(baseManifest), publicKey, null, baseZip, new(0, 1, 0));
    install.Begin();
    install.FinishTrial(true);
    var active = install.RunningPair;
    var activeDir = Path.GetDirectoryName(install.RuntimePath(active))!;
    var sameLauncher = MakeArchive(Path.Combine(temp, "current-launcher.zip"), "launcher", "0.1.0", PackageFiles(args[0]));
    var release = baseRelease with { Schema = 2, Launcher = sameLauncher, Runtime = targetPackage, RuntimePatches = [patch] };
    Require(install.SelectRuntimePatch(release, _ => false) is null);
    var signed = Serialize(release);
    var duplicate = Serialize(release with { RuntimePatches = [patch, patch] });
    Reject<InvalidDataException>(() => UpdateVerification.VerifyManifest(duplicate, Sign(duplicate), publicKey));
    var badTree = Serialize(release with { RuntimePatches = [patch with { TargetTreeSha256 = new string('0', 64) }] });
    Reject<InvalidDataException>(() => install.Stage(badTree, Sign(badTree), publicKey, null,
        targetZip, new(0, 1, 0)));
    Reject<InvalidDataException>(() => install.Stage(badTree, Sign(badTree), publicKey, null,
        null, new(0, 1, 0), new(patch.BaseVersion, patchPath)));
    Require(install.Read().Pending is null);

    using (var server = new FixtureReleases(new()
    {
        [UpdateVerification.ManifestName] = baseManifest,
        [UpdateVerification.SignatureName] = Sign(baseManifest)
    }))
    using (var github = new GitHubReleases(server))
        Require((await github.Check(publicKey, CancellationToken.None)).Release == baseRelease);

    var corrupt = File.ReadAllBytes(patchPath);
    corrupt[30] ^= 1;
    using (var server = NewServer(corrupt))
    using (var github = new GitHubReleases(server))
    {
        var offer = await github.Check(publicKey, CancellationToken.None);
        await RejectAsync<InvalidDataException>(() => github.Stage(offer, install, new(0, 1, 0), publicKey,
            CancellationToken.None));
        Require(!server.Requests.Contains(targetPackage.Asset) && install.Read().Pending is null);
    }

    using (var server = NewServer(File.ReadAllBytes(patchPath)))
    using (var github = new GitHubReleases(server))
    {
        var offer = await github.Check(publicKey, CancellationToken.None);
        Require(offer.Release.Schema == 2 && offer.Release.RuntimePatches!.Single() == patch);
        var staged = await github.Stage(offer, install, new(0, 1, 0), publicKey, CancellationToken.None);
        Require(server.Requests.Contains(patch.Asset) && !server.Requests.Contains(targetPackage.Asset) &&
            !server.Requests.Contains(sameLauncher.Asset));
        Require(install.Read().Active == active && install.Read().Pending == staged);
        CheckFiles(staged);
        Require(File.ReadAllBytes(Path.Combine(activeDir, "Renderer.dll")).SequenceEqual(original));
        var hold = Path.Combine(install.Root, "interrupt-fixture");
        File.WriteAllText(hold, "interrupt a patched trial");
        using (var bootstrap = StartBootstrap(install.Root))
        {
            var child = await WaitForObservation(install.Root);
            using var trial = Process.GetProcessById(child.Pid);
            bootstrap.Kill();
            await bootstrap.WaitForExitAsync();
            trial.Kill();
            await trial.WaitForExitAsync();
        }
        Require(install.Read().Trial == staged && install.Read().Active == active);
        File.Delete(hold);
        await RunBootstrap(install.Root);
        Require(install.Read().Trial is null && install.Read().Active == active && install.Read().Pending is null);
    }

    File.WriteAllBytes(Path.Combine(activeDir, "Common.dll"), [11, 12]);
    using (var server = NewServer(File.ReadAllBytes(patchPath)))
    using (var github = new GitHubReleases(server))
    {
        var offer = await github.Check(publicKey, CancellationToken.None);
        var staged = await github.Stage(offer, install, new(0, 1, 0), publicKey, CancellationToken.None);
        Require(server.Requests.IndexOf(patch.Asset) < server.Requests.IndexOf(targetPackage.Asset) &&
            server.Requests.Contains(patch.Asset));
        CheckFiles(staged);
        Require(install.Read().Active == active && install.Read().Pending == staged &&
            File.ReadAllBytes(Path.Combine(activeDir, "Common.dll")).SequenceEqual(new byte[] { 11, 12 }));
    }
    Console.WriteLine($"PASS signed block patch, shifted block reuse, corrupt-patch rejection, fallback and preserved base ({patch.Size:N0} vs {targetPackage.Size:N0} bytes)");

    FixtureReleases NewServer(byte[] patchBytes) => new(new()
    {
        [UpdateVerification.PatchManifestName] = signed,
        [UpdateVerification.PatchSignatureName] = Sign(signed),
        [patch.Asset] = patchBytes,
        [targetPackage.Asset] = File.ReadAllBytes(targetZip)
    });
    byte[] Sign(byte[] bytes) => key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    byte[] Serialize(ReleaseManifest value) => JsonSerializer.SerializeToUtf8Bytes(value, Storage.Json);
    void CheckFiles(Slots slots)
    {
        var output = Path.GetDirectoryName(install.RuntimePath(slots))!;
        var expected = Directory.GetFiles(target).Select(path => Path.GetFileName(path)!).Order().ToArray();
        Require(Directory.GetFiles(output).Select(path => Path.GetFileName(path)!).Order().SequenceEqual(expected));
        foreach (var name in expected)
            Require(File.ReadAllBytes(Path.Combine(target, name)).SequenceEqual(File.ReadAllBytes(Path.Combine(output, name))));
    }
}

static async Task<Observation> WaitForObservation(string root)
{
    var observed = Path.Combine(root, "observed.json");
    for (int tries = 0; tries < 100; tries++)
    {
        if (File.Exists(observed))
            return Storage.Read<Observation>(observed);
        await Task.Delay(50);
    }
    throw new TimeoutException("Fixture did not start.");
}

static Process StartBootstrap(string path)
{
    var start = new ProcessStartInfo(Path.Combine(path, "ProjectQuasar.exe")) { WorkingDirectory = path, UseShellExecute = false };
    start.ArgumentList.Add("--user-root");
    start.ArgumentList.Add(path + "-player");
    return Process.Start(start)!;
}

static async Task RunBootstrap(string path)
{
    using var bootstrap = StartBootstrap(path);
    await FinishBootstrap(bootstrap);
}

static async Task FinishBootstrap(Process bootstrap)
{
    try { await bootstrap.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
    finally
    {
        if (!bootstrap.HasExited)
        {
            bootstrap.Kill(entireProcessTree: true);
            await bootstrap.WaitForExitAsync();
        }
    }
    Require(bootstrap.ExitCode == 0);
}

static IEnumerable<(string Name, byte[] Data)> PackageFiles(string source) =>
    Directory.EnumerateFiles(source).Where(path => Path.GetExtension(path) != ".pdb" &&
        Path.GetFileName(path) != "Core.deps.json")
        .Select(path => (Path.GetFileName(path), File.ReadAllBytes(path)));

static ReleasePackage MakeArchive(string path, string kind, string version, IEnumerable<(string Name, byte[] Data)> entries)
{
    long expanded = 0;
    using (var file = File.Create(path))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        foreach (var (name, data) in entries)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(data);
            expanded += data.Length;
        }
    using var input = File.OpenRead(path);
    return new(version, $"quasar-{kind}-{version}-win-x64.zip", input.Length, expanded,
        Convert.ToHexString(SHA256.HashData(input)));
}

static void FileCopy(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
}

static byte[] Xbe(uint title)
{
    var bytes = new byte[0x180];
    "XBEH"u8.CopyTo(bytes);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x104), 0x10000);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x108), (uint)bytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x118), 0x10120);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x128), title);
    return bytes;
}

static void WriteImage(string path, byte[] xbe)
{
    var data = new byte[36 * 2048];
    var header = data.AsSpan(32 * 2048, 2048);
    "MICROSOFT*XBOX*MEDIA"u8.CopyTo(header);
    "MICROSOFT*XBOX*MEDIA"u8.CopyTo(header[^20..]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 33);
    BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 2048);
    var root = data.AsSpan(33 * 2048);
    BinaryPrimitives.WriteUInt32LittleEndian(root[4..], 34);
    BinaryPrimitives.WriteUInt32LittleEndian(root[8..], (uint)xbe.Length);
    root[13] = 11;
    "default.xbe"u8.CopyTo(root[14..]);
    xbe.CopyTo(data, 34 * 2048);
    File.WriteAllBytes(path, data);
}

static void Require(bool ok, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(ok))] string? name = null)
{
    if (!ok)
        throw new InvalidOperationException($"Fixture assertion failed: {name}");
}

static void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task RejectAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

internal sealed record LaunchProof(string WorkingDirectory, string[] Arguments, string? InheritedDiagnostic);
internal sealed record Observation(int Pid, string Slot, string Version);

internal sealed class FixtureInput : IInputSource
{
    public string[] Descriptions => ["Original controller hardware verification pending.",
        "Keyboard and mouse", "Xbox-compatible gamepad 1 (fixture)"];
    public string? FirstJoystick => null;
    public InputFrame Poll(bool focused) => new(["xinput:0"], [new("xinput:0", "RX", 0.42, true)]);
    public void Dispose() { }
}

internal sealed class FixtureReleases(Dictionary<string, byte[]> assets) : HttpMessageHandler
{
    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (address.Host == "api.github.com")
        {
            var listing = assets.Keys.Select(name => new
            {
                name,
                browser_download_url = "https://github.com/cstout816/project-quasar/releases/download/fixture/" + name
            });
            response.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { assets = listing }));
        }
        else
        {
            var name = address.Segments[^1];
            Requests.Add(name);
            if (assets.TryGetValue(name, out var bytes))
                response.Content = new ByteArrayContent(bytes);
            else
                response.StatusCode = HttpStatusCode.NotFound;
        }
        return Task.FromResult(response);
    }
}
