namespace Quasar;

public static class LauncherPaths
{
    public static string ResolveUserRoot(string? path, string applicationRoot)
    {
        var directory = path ?? Storage.UserRoot;
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidDataException("Pass an absolute directory to --user-root.");
        directory = Path.GetFullPath(directory);
        Storage.RequireOrdinaryPath(directory);
        if (Storage.Under(directory, applicationRoot) || Storage.Under(applicationRoot, directory))
            throw new InvalidDataException("Keep launcher user data and the application in separate directories.");
        return directory;
    }
}
