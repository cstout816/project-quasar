using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;

namespace Quasar;

public sealed record RuntimeManifest(int Schema, string Version, string Platform, string Executable,
    string WorkingDirectory, string[] DataFormats, string[] Capabilities, string MinimumLauncher,
    string MaximumLauncherExclusive)
{
    public const int Contract = 1;
    public const string Target = "win-x64";
    public static readonly string[] Features = ["input-profile-v1", "original-640x480", "borderless",
        "fullscreen", "output-resolution", "widescreen", "vsync", "frame-cap"];

    public static RuntimeManifest Load(string path, Version launcher)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Select an absolute runtime manifest path.");
        var runtime = Storage.Read<RuntimeManifest>(path);
        if (runtime.Schema != Contract || runtime.Platform != Target || !System.Version.TryParse(runtime.Version, out _) ||
            !InRange(launcher, runtime.MinimumLauncher, runtime.MaximumLauncherExclusive) ||
            runtime.DataFormats is not { Length: > 0 } ||
            runtime.DataFormats.Any(format => format is not ("xdvdfs" or "extracted")) ||
            runtime.Capabilities is null || runtime.Capabilities.Any(feature => !Features.Contains(feature)) ||
            runtime.Capabilities.Intersect(Features.Skip(2)).Any() && !runtime.Supports("original-640x480"))
            throw new InvalidDataException("The runtime manifest is incompatible with this launcher.");
        runtime.Resolve(path);
        return runtime;
    }

    public bool Supports(string capability) => Capabilities.Contains(capability);

    public (string Exe, string Working) Resolve(string path)
    {
        var root = Path.GetDirectoryName(path)!;
        var exe = Storage.Within(root, Executable);
        var work = Storage.Within(root, WorkingDirectory);
        Storage.RequireOrdinaryPath(path);
        Storage.RequireOrdinaryPath(exe);
        Storage.RequireOrdinaryPath(work);
        if (!File.Exists(exe) || !Directory.Exists(work) ||
            !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime executable or working directory is missing.");

        // Diagnostic entry points and retained candidate trees belong to the diagnostic guard.
        if (exe.Split(Path.DirectorySeparatorChar).Any(
                part => part.Equals("ProducerTrace-Candidates", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("your_game_recomp", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Select a contract-enabled runtime outside the diagnostic candidate trees.");
        using var image = new PEReader(File.OpenRead(exe));
        if (image.PEHeaders.CoffHeader.Machine != Machine.Amd64 || image.PEHeaders.PEHeader is null ||
            image.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
            throw new InvalidDataException("The selected runtime is not a Windows x64 executable.");
        return (exe, work);
    }

    public static bool InRange(Version version, string min, string max) =>
        System.Version.TryParse(min, out var lower) && System.Version.TryParse(max, out var upper)
        && version >= lower && version < upper;

    public static Version Installed(Assembly assembly) =>
        new(assembly.GetName().Version!.ToString(3));
}

public static class RuntimeLaunch
{
    public static ProcessStartInfo Prepare(string manifestPath, RuntimeManifest runtime, string data,
        string userRoot, string profilePath, Graphics requested, params string[] applicationRoots)
    {
        var (exe, directory) = runtime.Resolve(manifestPath);
        var dataFormat = GameData.Validate(data);
        Storage.RequireOrdinaryPath(data);
        Storage.RequireOrdinaryPath(userRoot);
        Storage.RequireOrdinaryPath(Path.Combine(userRoot, "Saves"));
        Storage.RequireOrdinaryPath(profilePath);
        if (!runtime.DataFormats.Contains(dataFormat))
            throw new InvalidDataException("The selected runtime cannot read this game-data format.");

        var roots = applicationRoots.Append(Path.GetDirectoryName(manifestPath)!);
        if (roots.Any(root => Storage.Under(root, data) || Storage.Under(root, userRoot) ||
            Directory.Exists(data) && Storage.Under(data, root)))
            throw new InvalidDataException("Keep game data and saves outside application directories.");

        var settings = SupportedGraphics(runtime, requested);
        var info = new ProcessStartInfo(exe) { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var key in info.Environment.Keys.Where(key =>
            key.StartsWith("RECOMP_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("QUASAR_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        foreach (var argument in new[] { "--quasar-contract", "1", "--game-data", data,
            "--game-data-format", dataFormat, "--save-dir", Path.Combine(userRoot, "Saves") })
            info.ArgumentList.Add(argument);

        if (runtime.Supports("input-profile-v1"))
        {
            info.ArgumentList.Add("--input-profile");
            info.ArgumentList.Add(profilePath);
        }
        if (runtime.Supports("original-640x480"))
        {
            info.ArgumentList.Add("--graphics-json");
            info.ArgumentList.Add(System.Text.Json.JsonSerializer.Serialize(settings, Storage.Json));
        }
        return info;
    }

    public static Graphics SupportedGraphics(RuntimeManifest runtime, Graphics graphics) => new(
        Mode: graphics.Mode switch
        {
            "borderless" when runtime.Supports("borderless") => "borderless",
            "fullscreen" when runtime.Supports("fullscreen") => "fullscreen",
            _ => "windowed"
        },
        Width: graphics.Experimental && runtime.Supports("output-resolution")
            ? Math.Clamp(graphics.Width, 640, 7680) : 640,
        Height: graphics.Experimental && runtime.Supports("output-resolution")
            ? Math.Clamp(graphics.Height, 480, 4320) : 480,
        Aspect: graphics.Experimental && runtime.Supports("widescreen") && graphics.Aspect == "16:9" ? "16:9" : "4:3",
        VSync: graphics.Experimental && runtime.Supports("vsync") && graphics.VSync,
        FrameCap: graphics.Experimental && runtime.Supports("frame-cap") ? Math.Clamp(graphics.FrameCap, 0, 240) : 0,
        Experimental: graphics.Experimental && runtime.Capabilities.Intersect(RuntimeManifest.Features.Skip(4)).Any());
}

public sealed record RuntimeLease(int Pid, long StartedUtc)
{
    private static string PathFor(string root) => Path.Combine(root, "runtime-process.json");

    public static void Save(string root, Process process) =>
        Storage.Write(PathFor(root), new RuntimeLease(process.Id, process.StartTime.ToUniversalTime().Ticks));

    public static bool IsRunning(string root)
    {
        if (!File.Exists(PathFor(root)))
            return false;
        var lease = Storage.Read<RuntimeLease>(PathFor(root));
        try
        {
            using var process = Process.GetProcessById(lease.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.StartedUtc;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
