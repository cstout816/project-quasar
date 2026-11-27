using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quasar;

public sealed record RuntimePatchPackage(string BaseVersion, string Asset, long Size, string Sha256, string TargetTreeSha256);
public sealed record RuntimePatchDownload(string BaseVersion, string Archive);

public sealed class PatchBaseMismatchException(string file)
    : IOException($"The installed runtime file '{file}' does not match the patch base.");

internal sealed record PatchIndex(int Schema, string BaseVersion, string TargetVersion, PatchFile[] Files);
internal sealed record PatchFile(string Name, long Length, string Sha256, string? BaseSha256 = null,
    long BaseLength = 0, int[]? Blocks = null);

public static class RuntimePatch
{
    private const string IndexName = "runtime-patch.json";
    private const int IndexLimit = 4 * 1024 * 1024;
    private const int BlockSize = 16 * 1024;
    private const int Literal = -1;

    public static string AssetName(string from, string to) => $"quasar-runtime-{from}-to-{to}-win-x64.patch.zip";

    public static RuntimePatchPackage Create(string baseDirectory, string targetDirectory, string output)
    {
        var old = ReleaseFiles(baseDirectory);
        var target = ReleaseFiles(targetDirectory);
        if (Version.Parse(old.Version) >= Version.Parse(target.Version))
            throw new InvalidDataException("A patch must advance the runtime version.");
        if (Path.GetFileName(output) != AssetName(old.Version, target.Version))
            throw new InvalidDataException("The patch filename must match the base and target versions.");
        if (Storage.Under(baseDirectory, output) || Storage.Under(targetDirectory, output))
            throw new InvalidDataException("Write the patch outside the release directories.");

        var recipes = new List<PatchFile>();
        var file = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using (file)
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var path in target.Paths)
                    {
                        var name = Path.GetFileName(path);
                        using var updated = File.OpenRead(path);
                        using var original = old.ByName.TryGetValue(name, out var originalPath) ? File.OpenRead(originalPath) : null;
                        var recipe = BuildFile(name, original, updated);
                        recipes.Add(recipe);
                        if (recipe.BaseSha256 is null || recipe.Blocks is not null)
                        {
                            using var payload = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                            WriteLiterals(recipe, updated, payload);
                        }
                    }
                    using var index = zip.CreateEntry(IndexName, CompressionLevel.Optimal).Open();
                    JsonSerializer.Serialize(index, new PatchIndex(1, old.Version, target.Version, recipes.ToArray()), Storage.Json);
                }
                if (file.Length > UpdateVerification.PackageLimit)
                    throw new InvalidDataException("The patch exceeds the package size limit.");
                file.Position = 0;
                return new(old.Version, Path.GetFileName(output), file.Length,
                    Convert.ToHexString(SHA256.HashData(file)), TreeHash(recipes));
            }
        }
        catch
        {
            File.Delete(output);
            throw;
        }
    }

    public static void Apply(RuntimePatchPackage patch, ReleasePackage target, string archivePath,
        string baseDirectory, string output)
    {
        using var file = File.OpenRead(archivePath);
        UpdateVerification.VerifyHash(file, patch.Size, patch.Sha256);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 1 or > 257)
            throw new InvalidDataException("Invalid patch entry count.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if ((entry.FullName != IndexName && !UpdateVerification.AllowedName(entry.FullName, launcher: false)) ||
                !UpdateVerification.IsRegularEntry(entry) || !uniqueNames.Add(entry.FullName))
                throw new InvalidDataException("Unsafe or duplicate patch entry.");
            entries.Add(entry.FullName, entry);
        }

        if (!entries.Remove(IndexName, out var indexEntry) || indexEntry.Length is < 1 or > IndexLimit)
            throw new InvalidDataException("The patch is missing a valid index.");
        PatchIndex index;
        using (var data = indexEntry.Open())
        {
            var bytes = new byte[(int)indexEntry.Length];
            data.ReadExactly(bytes);
            if (data.ReadByte() != -1)
                throw new InvalidDataException("Patch index exceeds its declared size.");
            index = Storage.Parse<PatchIndex>(bytes);
        }
        if (index.Schema != 1 || index.BaseVersion != patch.BaseVersion || index.TargetVersion != target.Version ||
            index.Files is not { Length: > 0 and <= 256 })
            throw new InvalidDataException("Unexpected patch index schema or version.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long remaining = target.ExpandedSize;
        foreach (var recipe in index.Files)
        {
            if (recipe is null || recipe.Name is null || !UpdateVerification.AllowedName(recipe.Name, launcher: false) ||
                !seen.Add(recipe.Name) || recipe.Length < 0 || recipe.Length > remaining ||
                !UpdateVerification.IsSha256(recipe.Sha256))
                throw new InvalidDataException("Invalid patched file metadata.");
            remaining -= recipe.Length;
            var literalLength = ValidateRecipe(recipe);
            var hasEntry = entries.Remove(recipe.Name, out var payload);
            if (hasEntry != (literalLength is not null) || (hasEntry && payload!.Length != literalLength))
                throw new InvalidDataException("Patch payload size differs from its recipe.");
        }
        if (remaining != 0 || entries.Count != 0 || !index.Files.Any(recipe => recipe.Name == "quasar-runtime.json"))
            throw new InvalidDataException("Patch files do not match the signed target package.");
        VerifyTreeHash(index.Files, patch.TargetTreeSha256);

        Directory.CreateDirectory(output);
        foreach (var recipe in index.Files)
        {
            using var original = recipe.BaseSha256 is null ? null : OpenBase(baseDirectory, recipe);
            using var payload = zip.GetEntry(recipe.Name)?.Open();
            var path = Path.Combine(output, recipe.Name);
            using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (recipe.Blocks is null)
                    CopyExactly(original ?? payload!, destination, recipe.Length);
                else
                    ApplyBlocks(recipe, original!, payload!, destination);
                if (payload is not null && payload.ReadByte() != -1)
                    throw new InvalidDataException("Patch payload exceeds its declared size.");
                destination.Flush(flushToDisk: true);
            }
            using var generated = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(generated)).Equals(recipe.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Patched file failed SHA-256 verification: {recipe.Name}");
        }
    }

    internal static void VerifyTargetTree(string directory, string hash)
    {
        var files = ReleaseFiles(directory);
        VerifyTreeHash(files.Paths.Select(path =>
        {
            using var file = File.OpenRead(path);
            return new PatchFile(Path.GetFileName(path), file.Length, Convert.ToHexString(SHA256.HashData(file)));
        }), hash);
    }

    private static void VerifyTreeHash(IEnumerable<PatchFile> files, string expected)
    {
        if (!TreeHash(files).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The runtime files differ from the signed target tree.");
    }

    private static string TreeHash(IEnumerable<PatchFile> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.ASCII.GetBytes(file.Name + "\0"));
            BinaryPrimitives.WriteInt64LittleEndian(length, file.Length);
            sha.AppendData(length);
            sha.AppendData(Convert.FromHexString(file.Sha256));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static long? ValidateRecipe(PatchFile recipe)
    {
        if (recipe.BaseSha256 is null)
        {
            if (recipe.BaseLength != 0 || recipe.Blocks is not null)
                throw new InvalidDataException("A replacement file cannot reference a base.");
            return recipe.Length;
        }
        if (!UpdateVerification.IsSha256(recipe.BaseSha256) || recipe.BaseLength < 0 ||
            recipe.BaseLength > UpdateVerification.PackageLimit)
            throw new InvalidDataException("Invalid patch base metadata.");
        if (recipe.Blocks is null)
        {
            if (recipe.BaseLength != recipe.Length ||
                !recipe.BaseSha256.Equals(recipe.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A reused file must match both fingerprints.");
            return null;
        }
        if (recipe.Blocks.Length != BlockCount(recipe.Length))
            throw new InvalidDataException("Wrong number of blocks in the patch recipe.");
        long literalLength = 0;
        for (int index = 0; index < recipe.Blocks.Length; index++)
        {
            var reference = recipe.Blocks[index];
            var length = BytesInBlock(recipe.Length, index);
            if (reference == Literal)
                literalLength += length;
            else if (reference < 0 || (long)reference * BlockSize + length > recipe.BaseLength)
                throw new InvalidDataException("Patch references a block outside its base file.");
        }
        return literalLength;
    }

    private static FileStream OpenBase(string directory, PatchFile recipe)
    {
        var path = Path.Combine(directory, recipe.Name);
        Storage.RequireOrdinaryPath(path);
        FileStream input;
        try { input = File.OpenRead(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PatchBaseMismatchException(recipe.Name);
        }
        try
        {
            if (input.Length != recipe.BaseLength ||
                !Convert.ToHexString(SHA256.HashData(input)).Equals(recipe.BaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new PatchBaseMismatchException(recipe.Name);
            input.Position = 0;
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static PatchFile BuildFile(string name, FileStream? original, FileStream updated)
    {
        var targetHash = Convert.ToHexString(SHA256.HashData(updated));
        var replacement = new PatchFile(name, updated.Length, targetHash);
        if (original is null)
            return replacement;
        var baseHash = Convert.ToHexString(SHA256.HashData(original));
        if (baseHash == targetHash && original.Length == updated.Length)
            return replacement with { BaseSha256 = baseHash, BaseLength = original.Length };
        if (Math.Min(original.Length, updated.Length) < BlockSize)
            return replacement;

        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        var buffer = new byte[BlockSize];
        original.Position = updated.Position = 0;
        for (var block = 0; block < BlockCount(original.Length); block++)
            lookup.TryAdd(HashBlock(original, buffer.AsSpan(0, BytesInBlock(original.Length, block))), block);

        var recipe = new int[BlockCount(updated.Length)];
        for (var block = 0; block < recipe.Length; block++)
        {
            var hash = HashBlock(updated, buffer.AsSpan(0, BytesInBlock(updated.Length, block)));
            recipe[block] = lookup.TryGetValue(hash, out var reference) ? reference : Literal;
        }

        return recipe.All(block => block == Literal) ? replacement :
            replacement with { BaseSha256 = baseHash, BaseLength = original.Length, Blocks = recipe };
    }

    private static string HashBlock(Stream input, Span<byte> bytes)
    {
        input.ReadExactly(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void WriteLiterals(PatchFile recipe, Stream target, Stream output)
    {
        target.Position = 0;
        if (recipe.Blocks is null)
        {
            CopyExactly(target, output, recipe.Length);
            return;
        }
        var buffer = new byte[BlockSize];
        for (var block = 0; block < recipe.Blocks.Length; block++)
        {
            if (recipe.Blocks[block] != Literal)
                continue;
            target.Position = (long)block * BlockSize;
            var data = buffer.AsSpan(0, BytesInBlock(recipe.Length, block));
            target.ReadExactly(data);
            output.Write(data);
        }
    }

    private static void ApplyBlocks(PatchFile recipe, Stream original, Stream payload, Stream destination)
    {
        var buffer = new byte[BlockSize];
        for (var block = 0; block < recipe.Blocks!.Length; block++)
        {
            var reference = recipe.Blocks[block];
            var source = reference == Literal ? payload : original;
            if (reference != Literal)
                original.Position = (long)reference * BlockSize;
            var bytes = buffer.AsSpan(0, BytesInBlock(recipe.Length, block));
            source.ReadExactly(bytes);
            destination.Write(bytes);
        }
    }

    private static void CopyExactly(Stream source, Stream target, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var bytes = buffer.AsSpan(0, (int)Math.Min(count, buffer.Length));
            source.ReadExactly(bytes);
            target.Write(bytes);
            count -= bytes.Length;
        }
    }

    private static int BlockCount(long length) => (int)((length + BlockSize - 1) / BlockSize);
    private static int BytesInBlock(long length, int block) => (int)Math.Min(BlockSize, length - (long)block * BlockSize);

    private sealed record ReleaseTree(string Version, string[] Paths, Dictionary<string, string> ByName);

    private static ReleaseTree ReleaseFiles(string directory)
    {
        Storage.RequireOrdinaryPath(directory);
        var paths = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length is < 1 or > 256 || Directory.EnumerateDirectories(directory).Any() ||
            paths.Any(path => !UpdateVerification.AllowedName(Path.GetFileName(path), launcher: false)))
            throw new InvalidDataException("Pass a flat, audited runtime release directory.");
        foreach (var path in paths)
            Storage.RequireOrdinaryPath(path);
        if (paths.Sum(path => new FileInfo(path).Length) > UpdateVerification.PackageLimit)
            throw new InvalidDataException("Runtime release files exceed the size limit.");
        var manifest = Storage.Read<RuntimeManifest>(Path.Combine(directory, "quasar-runtime.json"));
        if (!UpdateVerification.IsCanonicalVersion(manifest.Version))
            throw new InvalidDataException("Runtime release manifests must use canonical three-part versions.");
        return new(manifest.Version, paths, paths.ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase));
    }
}
