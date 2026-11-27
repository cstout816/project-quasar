using System.Text.Json;

namespace Quasar;

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static T Read<T>(string path, int limit = 1024 * 1024) =>
        Parse<T>(ReadBytes(path, limit));

    public static byte[] ReadBytes(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit)
            throw new InvalidDataException("File exceeds the size limit.");
        var buffer = new byte[checked((int)stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public static T Parse<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json)
        ?? throw new InvalidDataException("The JSON document is empty.");

    public static void Write<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static bool Under(string parent, string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Expected a relative path.");
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!Under(root, resolved))
            throw new InvalidDataException("Path leaves its package directory.");
        return resolved;
    }

    public static void RequireOrdinaryPath(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) &&
                (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Choose a path without links or junctions.");
    }

    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Project Quasar");
}

public sealed record Graphics(string Mode = "windowed", int Width = 640, int Height = 480,
    string Aspect = "4:3", bool VSync = false, int FrameCap = 0, bool Experimental = false)
{
    public static readonly Graphics Original = new();
}

public sealed record Settings(int Schema = 2, string GameData = "", string DevelopmentManifest = "",
    bool AutomaticChecks = false, Graphics? Graphics = null)
{
    public static Settings Load(string path)
    {
        if (!File.Exists(path))
            return new();
        var bytes = Storage.ReadBytes(path, 1024 * 1024);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.GetProperty("schema").GetInt32() switch
        {
            1 => Migrate(Storage.Parse<SettingsV1>(bytes)),
            2 => Storage.Parse<Settings>(bytes),
            _ => throw new InvalidDataException("Unsupported launcher settings version.")
        };
    }

    private static Settings Migrate(SettingsV1 old) =>
        new(GameData: old.GameData, DevelopmentManifest: old.RuntimeManifest);

    private sealed record SettingsV1(int Schema, string GameData = "", string RuntimeManifest = "");
}
