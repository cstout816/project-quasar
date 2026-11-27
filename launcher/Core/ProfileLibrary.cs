namespace Quasar;

public sealed record NamedProfile(Guid Id, InputProfile Profile);

public sealed record ProfileLibrary(int Schema, Guid Selected, NamedProfile[] Entries)
{
    public const int Capacity = 24;
    [System.Text.Json.Serialization.JsonIgnore]
    public NamedProfile Active => Entries.Single(entry => entry.Id == Selected);

    public static ProfileLibrary Load(string catalog, string legacy)
    {
        if (!File.Exists(catalog))
            return Empty(File.Exists(legacy) ? InputProfile.Load(legacy) : InputProfile.Keyboard());
        return Storage.Read<ProfileLibrary>(catalog, 8 * 1024 * 1024).Validate();
    }

    public static ProfileLibrary Empty(InputProfile profile)
    {
        var id = Guid.NewGuid();
        return new(1, id, [new(id, profile)]);
    }

    public ProfileLibrary Select(Guid id) =>
        (this with { Selected = id }).Validate();

    public ProfileLibrary Replace(InputProfile profile) =>
        (this with { Entries = Entries.Select(entry => entry.Id == Selected ? entry with { Profile = profile } : entry).ToArray() })
        .Validate();

    public ProfileLibrary Add(InputProfile profile)
    {
        profile.Validate();
        if (Entries.Length == Capacity)
            throw new InvalidDataException($"The library holds at most {Capacity} profiles. Reuse an existing profile.");
        var id = Guid.NewGuid();
        var name = profile.Name;
        for (int number = 2; Entries.Any(entry => SameName(entry.Profile.Name, name)); number++)
        {
            var suffix = $" ({number})";
            name = profile.Name[..Math.Min(profile.Name.Length, 80 - suffix.Length)] + suffix;
        }
        return (this with { Selected = id, Entries = [.. Entries, new(id, profile with { Name = name })] }).Validate();
    }

    private ProfileLibrary Validate()
    {
        if (Schema != 1 || Entries is not { Length: > 0 and <= Capacity })
            throw new InvalidDataException("Unsupported profile library or profile count.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (entry is null || entry.Profile is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) ||
                !names.Add(entry.Profile.Validate().Name.Trim()))
                throw new InvalidDataException("Each profile needs a unique name and identifier.");
        }
        if (!ids.Contains(Selected))
            throw new InvalidDataException("The selected profile is absent from the library.");
        return this;
    }

    private static bool SameName(string left, string right) =>
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
}
