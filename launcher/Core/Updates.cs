using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Quasar;

public sealed record ReleaseManifest(int Schema, string Platform, int Contract,
    string MinimumRuntime, string MaximumRuntimeExclusive, ReleasePackage? Launcher = null, ReleasePackage? Runtime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RuntimePatchPackage[]? RuntimePatches = null);

public sealed record ReleasePackage(string Version, string Asset, long Size, long ExpandedSize, string Sha256);
public sealed record UpdatePlan(ReleasePackage? Launcher, ReleasePackage? Runtime, Version LauncherVersion, Version? RuntimeVersion);

public static partial class UpdateVerification
{
    public const int ManifestLimit = 64 * 1024;
    public const long PackageLimit = 768L * 1024 * 1024;
    public const string ManifestName = "quasar-win-x64.json";
    public const string SignatureName = "quasar-win-x64.sig";
    public const string PatchManifestName = "quasar-win-x64-v2.json";
    public const string PatchSignatureName = "quasar-win-x64-v2.sig";
    private static readonly string[] LauncherFiles = ["Quasar.Launcher.exe", "Quasar.Launcher.dll",
        "Quasar.Launcher.deps.json", "Quasar.Launcher.runtimeconfig.json", "Core.dll"];

    public static ReleaseManifest VerifyManifest(byte[] manifest, byte[] signature, string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidOperationException("Updates are unavailable until the maintainer embeds the release public key.");
        if (manifest.Length > ManifestLimit || signature.Length != 384)
            throw new InvalidDataException("Invalid release manifest or signature size.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (rsa.KeySize != 3072 || !rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("Release manifest signature verification failed.");
        var release = Storage.Parse<ReleaseManifest>(manifest);
        if (release.Schema is not (1 or 2) || release.Platform != RuntimeManifest.Target || release.Contract != RuntimeManifest.Contract ||
            (release.Launcher is null && release.Runtime is null) ||
            !Version.TryParse(release.MinimumRuntime, out var min) ||
            !Version.TryParse(release.MaximumRuntimeExclusive, out var max) || min >= max)
            throw new InvalidDataException("Unsupported release manifest.");
        Validate(release.Launcher, "launcher");
        Validate(release.Runtime, "runtime");
        if (release.RuntimePatches is { } patches)
        {
            if (release.Schema != 2 || release.Runtime is not { } target || patches.Length is < 1 or > 16)
                throw new InvalidDataException("Runtime patches require a schema 2 manifest and a full target package.");
            var baseVersions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var patch in patches)
                if (patch is null || !IsCanonicalVersion(patch.BaseVersion) ||
                    Version.Parse(patch.BaseVersion) >= Version.Parse(target.Version) || !baseVersions.Add(patch.BaseVersion) ||
                    patch.Asset != RuntimePatch.AssetName(patch.BaseVersion, target.Version) ||
                    patch.Size is <= 0 or > PackageLimit || !IsSha256(patch.Sha256) ||
                    !IsSha256(patch.TargetTreeSha256) ||
                    !patch.TargetTreeSha256.Equals(patches[0].TargetTreeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid signed runtime patch metadata.");
        }
        return release;
    }

    private static void Validate(ReleasePackage? package, string kind)
    {
        if (package is null)
            return;
        if (!IsCanonicalVersion(package.Version) || package.Asset != $"quasar-{kind}-{package.Version}-win-x64.zip" ||
            package.Size is <= 0 or > PackageLimit || package.ExpandedSize is <= 0 or > PackageLimit ||
            !IsSha256(package.Sha256))
            throw new InvalidDataException($"Invalid signed {kind} metadata.");
    }

    public static void Extract(ReleasePackage package, string zipPath, string directory, bool launcher)
    {
        using var zip = File.OpenRead(zipPath);
        VerifyHash(zip, package.Size, package.Sha256);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count is < 1 or > 256)
            throw new InvalidDataException("Unsupported archive entry count.");
        long capacity = package.ExpandedSize;
        foreach (var entry in archive.Entries)
        {
            if (!AllowedName(entry.FullName, launcher) || !files.Add(entry.FullName) ||
                !IsRegularEntry(entry) || entry.Length > capacity)
                throw new InvalidDataException("Unsafe, duplicate or oversized archive entry.");
            capacity -= entry.Length;
        }
        if (capacity != 0 || (launcher && !LauncherFiles.All(files.Contains)) ||
            (!launcher && !files.Contains("quasar-runtime.json")))
            throw new InvalidDataException("Package contents do not match signed metadata.");

        Directory.CreateDirectory(directory);
        foreach (var entry in archive.Entries)
        {
            using var source = entry.Open();
            using var destination = new FileStream(Path.Combine(directory, entry.FullName), FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[64 * 1024];
            long remaining = entry.Length;
            while (remaining > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw new InvalidDataException("Truncated archive entry.");
                destination.Write(buffer, 0, read);
                remaining -= read;
            }
            if (source.ReadByte() != -1)
                throw new InvalidDataException("Archive entry exceeded its declared size.");
            destination.Flush(flushToDisk: true);
        }
    }

    internal static bool IsCanonicalVersion(string? text) =>
        Version.TryParse(text, out var version) && version.Build >= 0 && version.Revision < 0 && text == version.ToString(3);

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void VerifyHash(FileStream stream, long size, string hash)
    {
        if (stream.Length != size ||
            !Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package length or SHA-256 mismatch.");
        stream.Position = 0;
    }

    internal static bool IsRegularEntry(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) is 0 or 0x8000 &&
        (entry.ExternalAttributes & 0x410) == 0;

    internal static bool AllowedName(string name, bool launcher)
    {
        if (name == "LICENSE.txt" || name == "NOTICE.txt" || name == "THIRD-PARTY-NOTICES.txt")
            return true;
        if (launcher)
            return LauncherFiles.Contains(name);
        return name == "quasar-runtime.json" || SafeRuntimeFile().IsMatch(name)
            && !ReservedName().IsMatch(name);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,100}\.(exe|dll|deps\.json|runtimeconfig\.json)\z", RegexOptions.IgnoreCase)]
    private static partial Regex SafeRuntimeFile();
    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}

public sealed record Slots(string Launcher = "bundled", string? Runtime = null,
    string? MinimumRuntime = null, string? MaximumRuntimeExclusive = null);
public sealed record InstallState(int Schema, Slots Active, Slots? Previous = null, Slots? Pending = null, Slots? Trial = null);
public sealed record LauncherReady(int Pid, string Version);

public sealed class Installation(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private string StatePath => Path.Combine(Root, "installation.json");
    private string Versions => Path.Combine(Root, "versions");
    public InstallState Read()
    {
        var state = File.Exists(StatePath) ? Storage.Read<InstallState>(StatePath) : new(1, new());
        if (state.Schema != 1 || state.Active is null)
            throw new InvalidDataException("Unsupported installation state.");
        return state;
    }

    private void Write(InstallState value) => Storage.Write(StatePath, value);

    public string LauncherPath(Slots pair) => Path.Combine(pair.Launcher == "bundled"
        ? Path.Combine(Root, "bundled") : SlotPath(pair.Launcher, "launcher"), "Quasar.Launcher.exe");

    public string? RuntimePath(Slots pair) => pair.Runtime is null ? null
        : Path.Combine(SlotPath(pair.Runtime, "runtime"), "quasar-runtime.json");

    public Version? RuntimeVersion(Slots pair)
    {
        if (pair.Runtime is not { } slot)
            return null;
        SlotPath(slot, "runtime");
        return Version.Parse(slot["runtime-".Length..^33]);
    }

    public RuntimePatchPackage? SelectRuntimePatch(ReleaseManifest release, Func<string, bool> isAvailable)
    {
        var current = RuntimeVersion(Read().Active)?.ToString();
        return release.RuntimePatches?.SingleOrDefault(patch => patch.BaseVersion == current &&
            release.Runtime is { } target && patch.Size < target.Size && isAvailable(patch.Asset));
    }

    public UpdatePlan Plan(ReleaseManifest release, Version currentLauncher, Slots active)
    {
        var currentRuntime = RuntimeVersion(active);
        var launcher = Advance(release.Launcher, currentLauncher);
        var runtime = Advance(release.Runtime, currentRuntime);
        if (launcher is null && runtime is null)
            throw new InvalidDataException("The installed versions are already current.");
        var launcherVersion = launcher is null ? currentLauncher : Version.Parse(launcher.Version);
        var runtimeVersion = runtime is null ? currentRuntime : Version.Parse(runtime.Version);
        if (runtimeVersion is not null &&
            (!RuntimeManifest.InRange(runtimeVersion, release.MinimumRuntime, release.MaximumRuntimeExclusive) ||
             launcher is null && active.MinimumRuntime is { } min &&
             !RuntimeManifest.InRange(runtimeVersion, min, active.MaximumRuntimeExclusive!)))
            throw new InvalidDataException("The launcher and runtime versions are incompatible.");
        if (runtime is null && RuntimePath(active) is { } installed)
            RuntimeManifest.Load(installed, launcherVersion);
        return new(launcher, runtime, launcherVersion, runtimeVersion);

        static ReleasePackage? Advance(ReleasePackage? package, Version? current)
        {
            if (package is null)
                return null;
            var proposed = Version.Parse(package.Version);
            if (current is not null && proposed < current)
                throw new InvalidDataException($"The release would downgrade {package.Asset}. Use rollback to restore a prior version.");
            return proposed == current ? null : package;
        }
    }

    public Slots Stage(byte[] manifest, byte[] signature, string? publicKey, string? launcherArchive,
        string? runtimeArchive, Version currentLauncher, RuntimePatchDownload? runtimePatch = null)
    {
        var release = UpdateVerification.VerifyManifest(manifest, signature, publicKey);
        var state = Read();
        if (state.Trial is not null || state.Pending is not null)
            throw new InvalidOperationException("Finish the pending restart before staging another update.");
        var plan = Plan(release, currentLauncher, state.Active);
        var runtimePath = RuntimePath(state.Active);

        RuntimePatchPackage? patch = null;
        if (runtimePatch is not null)
        {
            patch = release.RuntimePatches?.SingleOrDefault(candidate => candidate.BaseVersion == runtimePatch.BaseVersion);
            if (patch is null || plan.Runtime is null || runtimeArchive is not null)
                throw new InvalidDataException("The selected runtime patch is outside the signed release.");
            if (runtimePath is null || patch.BaseVersion != RuntimeVersion(state.Active)?.ToString())
                throw new PatchBaseMismatchException("quasar-runtime.json");
        }

        var pair = state.Active with
        {
            MinimumRuntime = plan.Launcher is null && state.Active.MinimumRuntime is not null
                ? state.Active.MinimumRuntime : release.MinimumRuntime,
            MaximumRuntimeExclusive = plan.Launcher is null && state.Active.MaximumRuntimeExclusive is not null
                ? state.Active.MaximumRuntimeExclusive : release.MaximumRuntimeExclusive
        };
        var stagedPaths = new List<string>();
        try
        {
            if (plan.Launcher is { } app)
                pair = pair with { Launcher = StagePackage(app, "launcher", stagedPaths,
                    output => ExtractFull(app, launcherArchive, output, launcher: true)) };
            if (plan.Runtime is { } target)
                pair = pair with { Runtime = StagePackage(target, "runtime", stagedPaths, output =>
                {
                    if (patch is not null)
                        RuntimePatch.Apply(patch, target, runtimePatch!.Archive, Path.GetDirectoryName(runtimePath!)!, output);
                    else
                    {
                        ExtractFull(target, runtimeArchive, output, launcher: false);
                        if (release.RuntimePatches is { Length: > 0 } definitions)
                            RuntimePatch.VerifyTargetTree(output, definitions[0].TargetTreeSha256);
                    }
                }) };

            if (plan.Runtime is not null)
            {
                var candidate = RuntimeManifest.Load(RuntimePath(pair)!, plan.LauncherVersion);
                if (candidate.Version != plan.Runtime.Version)
                    throw new InvalidDataException("Runtime manifest version differs from signed release metadata.");
            }
            Write(state with { Pending = pair });
            return pair;
        }
        catch
        {
            foreach (var path in stagedPaths)
                Directory.Delete(path, recursive: true);
            throw;
        }
    }

    public void QueueRollback()
    {
        var state = Read();
        if (state.Pending is not null || state.Trial is not null || state.Previous is null)
            throw new InvalidOperationException("Rollback needs a last-known-good version and no pending restart.");
        Write(state with { Pending = state.Previous });
    }

    public InstallState Begin()
    {
        var state = Read();
        // Keep the old pair active until the new launcher reports successful startup.
        if (state.Trial is not null)
        {
            state = state with { Trial = null, Pending = null };
            Write(state);
            return state;
        }
        if (state.Pending is not null)
        {
            state = state with { Trial = state.Pending, Pending = null };
            Write(state);
        }
        return state;
    }

    public void FinishTrial(bool healthy)
    {
        var state = Read();
        if (state.Trial is null)
            return;
        Write(healthy
            ? state with { Previous = state.Active, Active = state.Trial, Trial = null }
            : state with { Trial = null });
    }

    public Slots RunningPair
    {
        get
        {
            var state = Read();
            return state.Trial ?? state.Active;
        }
    }

    public static string? ExpectedLauncherVersion(Slots pair) => pair.Launcher == "bundled"
        ? null : pair.Launcher["launcher-".Length..^33];

    private static void ExtractFull(ReleasePackage package, string? archive, string output, bool launcher)
    {
        if (archive is null)
            throw new InvalidDataException($"Missing update archive {package.Asset}.");
        UpdateVerification.Extract(package, archive, output, launcher);
    }

    private string StagePackage(ReleasePackage package, string kind, List<string> stagedPaths, Action<string> materialize)
    {
        Storage.RequireOrdinaryPath(Versions);
        Directory.CreateDirectory(Versions);
        var name = $"{kind}-{package.Version}-{Guid.NewGuid():N}";
        var path = SlotPath(name, kind);
        var incoming = path + ".partial";
        try
        {
            materialize(incoming);
            Directory.Move(incoming, path);
            stagedPaths.Add(path);
            return name;
        }
        finally
        {
            if (Directory.Exists(incoming))
                Directory.Delete(incoming, recursive: true);
        }
    }

    private string SlotPath(string name, string kind)
    {
        var prefix = kind + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length <= prefix.Length + 33 || name[^33] != '-' ||
            !Guid.TryParseExact(name[^32..], "N", out _) ||
            !Version.TryParse(name[prefix.Length..^33], out _))
            throw new InvalidDataException("Invalid installation slot.");
        return Path.Combine(Versions, name);
    }
}
