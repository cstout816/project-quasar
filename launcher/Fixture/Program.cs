using Quasar;

if (args is ["--installation", var root, ..])
{
    var slot = new Installation(root).RunningPair.Launcher;
    var version = RuntimeManifest.Installed(typeof(Observation).Assembly).ToString();
    Storage.Write(Path.Combine(root, "observed.json"), new Observation(Environment.ProcessId, slot, version));
    var hold = Path.Combine(root, "hold-baseline-fixture");
    var readyIndex = Array.IndexOf(args, "--ready-file");
    if (slot == "bundled" && readyIndex < 0)
        for (int tries = 0; tries < 200 && File.Exists(hold); tries++)
            await Task.Delay(50);
    if (readyIndex >= 0)
    {
        if (File.Exists(Path.Combine(root, "interrupt-fixture")))
            await Task.Delay(TimeSpan.FromSeconds(20));
        Storage.Write(args[readyIndex + 1], new LauncherReady(Environment.ProcessId, version));
    }
    return;
}
Storage.Write(Path.Combine(Environment.CurrentDirectory, "launch-proof.json"),
    new LaunchProof(Environment.CurrentDirectory, args, Environment.GetEnvironmentVariable("RECOMP_FIXTURE")));

internal sealed record LaunchProof(string WorkingDirectory, string[] Arguments, string? InheritedDiagnostic);
internal sealed record Observation(int Pid, string Slot, string Version);
