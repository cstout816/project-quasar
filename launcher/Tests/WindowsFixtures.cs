using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Quasar;
using Quasar.Launcher;

internal static class DpiFixture
{
    public static void Scale(LauncherWindow window, int dpi)
    {
        // Run WinForms scaling against a simulated viewport without changing system display settings.
        var nativeDpi = window.DeviceDpi;
        if (nativeDpi != dpi)
        {
            var fonts = Descendants(window).Select(control => (Control: control, Font: control.Font)).ToArray();
            window.SuspendLayout();
            window.FixtureDpi = dpi;
            window.AutoScaleMode = AutoScaleMode.None;
            var ratio = dpi / (float)nativeDpi;
            window.Scale(new SizeF(ratio, ratio));
            foreach (var (control, source) in fonts)
            {
                var font = new Font(source.FontFamily, source.SizeInPoints * dpi / 72f, source.Style, GraphicsUnit.Pixel);
                control.Font = font;
                control.Disposed += (_, _) => font.Dispose();
            }
            window.ResumeLayout(true);
        }
        window.ClientSize = new(1080 * dpi / 96, 735 * dpi / 96);
        window.PerformLayout();
        Application.DoEvents();

        static IEnumerable<Control> Descendants(Control control) =>
            new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Descendants));
    }
}

internal static class PackagedSmoke
{
    public static async Task Run(string archive, string fixture, string temp, byte[] xbe)
    {
        var install = Path.Combine(temp, "portable install");
        var player = Path.Combine(temp, "portable player");
        var runtime = Path.Combine(temp, "portable runtime");
        var game = Path.Combine(temp, "portable game & synthetic");
        ZipFile.ExtractToDirectory(archive, install);
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(game);
        foreach (var path in Directory.EnumerateFiles(fixture))
            File.Copy(path, Path.Combine(runtime, Path.GetFileName(path)));
        File.WriteAllBytes(Path.Combine(game, "default.xbe"), xbe);
        var manifest = Path.Combine(runtime, "quasar-runtime.json");
        Storage.Write(manifest, new RuntimeManifest(1, "1.1.0", "win-x64", "Quasar.Launcher.exe", ".",
            ["extracted"], ["input-profile-v1", "original-640x480"], "0.1.0", "1.0.0"));
        Storage.Write(Path.Combine(player, "settings.json"), new Settings(GameData: game, DevelopmentManifest: manifest));

        var info = new ProcessStartInfo(Path.Combine(install, "ProjectQuasar.exe"))
        {
            UseShellExecute = false, WorkingDirectory = install
        };
        info.ArgumentList.Add("--user-root");
        info.ArgumentList.Add(player);
        using var bootstrap = Process.Start(info) ?? throw new InvalidOperationException("Portable bootstrap did not start.");
        try
        {
            var expected = Path.Combine(install, "bundled", "Quasar.Launcher.exe");
            using var launcher = await FindChild(expected, bootstrap);
            var window = launcher.MainWindowHandle;
            var play = FindButton(window, "Launch game");
            if (play == IntPtr.Zero || !IsWindowEnabled(play))
                throw new InvalidOperationException("The packaged launcher did not enable Play for the stub runtime.");
            SendMessageTimeout(play, 0x00F5, IntPtr.Zero, IntPtr.Zero, 2, 3000, out _); // BM_CLICK
            var proofPath = Path.Combine(runtime, "launch-proof.json");
            for (int count = 0; count < 100 && !File.Exists(proofPath); count++)
                await Task.Delay(50);
            var proof = Storage.Read<LaunchProof>(proofPath);
            if (proof.WorkingDirectory != runtime ||
                !proof.Arguments.Take(10).SequenceEqual(new[] {
                    "--quasar-contract", "1", "--game-data", game, "--game-data-format", "extracted",
                    "--save-dir", Path.Combine(player, "Saves"), "--input-profile", Path.Combine(player, "profiles", "active.json")
                }) || proof.Arguments[10] != "--graphics-json" || proof.InheritedDiagnostic is not null)
                throw new InvalidOperationException("The packaged launcher handed the stub unexpected arguments.");
            for (int count = 0; count < 100 && !IsWindowEnabled(play); count++)
                await Task.Delay(50);
            PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            await bootstrap.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (bootstrap.ExitCode != 0)
                throw new InvalidOperationException("The packaged bootstrap returned a failure.");
            Console.WriteLine("PASS packaged ProjectQuasar.exe started its app and launched the stub with isolated settings, data and saves");
        }
        finally
        {
            if (!bootstrap.HasExited)
            {
                bootstrap.Kill(entireProcessTree: true);
                await bootstrap.WaitForExitAsync();
            }
        }
    }

    private static async Task<Process> FindChild(string executable, Process bootstrap)
    {
        for (int attempts = 0; attempts < 100 && !bootstrap.HasExited; attempts++)
        {
            foreach (var process in Process.GetProcessesByName("Quasar.Launcher"))
            {
                bool matches;
                try
                {
                    matches = process.MainWindowHandle != IntPtr.Zero &&
                        process.MainModule?.FileName.Equals(executable, StringComparison.OrdinalIgnoreCase) == true;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    matches = false;
                }
                if (matches)
                    return process;
                process.Dispose();
            }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("The packaged launcher window did not open.");
    }

    private static IntPtr FindButton(IntPtr window, string name)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(window, (child, _) =>
        {
            var type = new StringBuilder(128);
            GetClassName(child, type, type.Capacity);
            if (!type.ToString().Contains(".BUTTON.", StringComparison.OrdinalIgnoreCase))
                return true;
            var text = new StringBuilder(128);
            SendMessageTimeout(child, 0x000D, (IntPtr)text.Capacity, text, 2, 500, out _); // WM_GETTEXT
            if (!text.ToString().Equals(name, StringComparison.Ordinal))
                return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
