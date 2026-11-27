using System.Diagnostics;
using Quasar;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        using var lease = new FileStream(Path.Combine(root, "bootstrap.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        try { lease.Lock(0, 1); }
        catch (IOException) { return 0; }
        try
        {
            var install = new Installation(root);
            var overrideRoot = args switch
            {
                [] => null,
                ["--user-root", var directory] => directory,
                _ => throw new InvalidDataException("Usage: ProjectQuasar.exe [--user-root <absolute path>]")
            };
            var userRoot = LauncherPaths.ResolveUserRoot(overrideRoot, root);
            do
            {
                var runtimeRunning = RuntimeLease.IsRunning(userRoot);
                var state = runtimeRunning ? install.Read() : install.Begin();
                if (runtimeRunning)
                {
                    using var existing = Start(install, state.Active, overrideRoot: overrideRoot);
                    existing.WaitForExit();
                    return 0;
                }
                if (state.Trial is not null)
                {
                    if (StartTrial(install, state.Trial, overrideRoot))
                        continue;
                    MessageBox.Show("The update did not start successfully. The last working version will open.",
                        "Project Quasar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                using var process = Start(install, install.Read().Active, overrideRoot: overrideRoot);
                process.WaitForExit();
            } while (install.Read().Pending is not null);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Project Quasar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static bool StartTrial(Installation install, Slots trial, string? overrideRoot)
    {
        var readyFile = Path.Combine(install.Root, $"ready-{Guid.NewGuid():N}.json");
        Process? process = null;
        bool healthy = false;
        try
        {
            process = Start(install, trial, readyFile, overrideRoot);
            var deadline = Stopwatch.StartNew();
            while (!process.HasExited && !File.Exists(readyFile) && deadline.Elapsed < TimeSpan.FromSeconds(30))
                process.WaitForExit(100);
            // A process may exit normally immediately after emitting readiness (e.g. user closes it).
            var expected = Installation.ExpectedLauncherVersion(trial);
            if (File.Exists(readyFile) && Storage.Read<LauncherReady>(readyFile) is var ready &&
                ready.Pid == process.Id && (expected is null || expected == ready.Version))
            {
                install.FinishTrial(true);
                healthy = true;
                process.WaitForExit();
                return true;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            // The old pair is still active. Fall back after the failed trial.
        }
        finally
        {
            if (!healthy && process is { HasExited: false })
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.WaitForExit();
            }
            process?.Dispose();
            install.FinishTrial(false);
            File.Delete(readyFile);
        }
        return false;
    }

    private static Process Start(Installation install, Slots pair, string? readyFile = null, string? overrideRoot = null)
    {
        var exe = install.LauncherPath(pair);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add("--installation");
        info.ArgumentList.Add(install.Root);
        if (readyFile is not null)
        {
            info.ArgumentList.Add("--ready-file");
            info.ArgumentList.Add(readyFile);
        }
        if (overrideRoot is not null)
        {
            info.ArgumentList.Add("--user-root");
            info.ArgumentList.Add(overrideRoot);
        }
        return Process.Start(info) ?? throw new IOException("Unable to start the launcher.");
    }
}
