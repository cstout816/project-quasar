namespace Quasar.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            string? installationRoot = null, userRoot = null, ready = null;
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                    throw new InvalidDataException("A launcher argument is missing its value.");
                switch (args[index])
                {
                    case "--installation" when installationRoot is null: installationRoot = args[index + 1]; break;
                    case "--user-root" when userRoot is null: userRoot = args[index + 1]; break;
                    case "--ready-file" when ready is null: ready = args[index + 1]; break;
                    default: throw new InvalidDataException("Unexpected launcher argument.");
                }
            }
            var installation = installationRoot is null ? null : new Installation(installationRoot);
            var root = LauncherPaths.ResolveUserRoot(userRoot, installation?.Root ?? AppContext.BaseDirectory);
            Directory.CreateDirectory(root);
            using var lease = new FileStream(Path.Combine(root, "launcher.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite);
            try { lease.Lock(0, 1); }
            catch (IOException) { return; }

            if (installation is not null &&
                !string.Equals(Path.GetFullPath(Environment.ProcessPath!), installation.LauncherPath(installation.RunningPair),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Open the launcher through ProjectQuasar.exe.");
            using var window = new LauncherWindow(root, installation);
            if (ready is not null && installation is not null &&
                Path.GetDirectoryName(Path.GetFullPath(ready))!.Equals(Path.TrimEndingDirectorySeparator(installation.Root), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(ready).StartsWith("ready-", StringComparison.Ordinal) && Path.GetExtension(ready) == ".json")
                window.Shown += (_, _) => Storage.Write(ready, new LauncherReady(Environment.ProcessId,
                    RuntimeManifest.Installed(typeof(Program).Assembly).ToString()));
            Application.Run(window);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Project Quasar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
