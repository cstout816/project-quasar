namespace Quasar.Launcher;

internal sealed partial class LauncherWindow
{
    private readonly CockpitCheckBox automaticUpdates = new() { Text = "Check for signed updates when the launcher opens", AutoSize = true };
    private readonly CockpitButton installUpdates = new() { Text = "Check for updates" };
    private readonly CockpitButton stageUpdate = new() { Text = "Download and stage", IsPrimary = true };
    private readonly CockpitButton restart = new() { Text = "Restart to apply", IsPrimary = true };
    private readonly CockpitButton rollback = new() { Text = "Stage last working version" };
    private readonly Label updateStatus = Caption(), updateOffer = Caption(), installedVersions = Caption(), recoveryStatus = Caption();
    private readonly ProgressBar transfer = new() { Height = 12, Width = 250, AccessibleName = "Update progress", Visible = false };
    private readonly string? publicKey = Trust.PublicKey;
    private GitHubReleases.Candidate? offer;
    private bool updateBusy;
    private bool applyingUpdate;

    private void BuildUpdates()
    {
        var panel = Page("Updates");
        installedVersions.Font = new("Consolas", 11);
        panel.Controls.Add(Module("Installed versions", installedVersions));
        panel.Controls.Add(Module("GitHub Releases", Caption("cstout816/project-quasar"), updateStatus,
            automaticUpdates, Row(installUpdates, stageUpdate), updateOffer, transfer));
        panel.Controls.Add(Module("Restart and rollback", recoveryStatus, Row(restart, rollback)));
        installUpdates.Click += async (_, _) => await CheckUpdates();
        stageUpdate.Click += async (_, _) => await StageUpdate();
        restart.Click += (_, _) => Close();
        rollback.Click += (_, _) => Run(() =>
        {
            installation!.QueueRollback();
            offer = null;
            updateOffer.Text = "Rollback is staged.";
            RefreshUpdates();
        });
        automaticUpdates.CheckedChanged += (_, _) =>
        {
            if (!refreshing)
                Run(() => ChangeSettings(settings with { AutomaticChecks = automaticUpdates.Checked }));
        };
    }

    private void RefreshUpdates()
    {
        refreshing = true;
        try
        {
            var state = installation?.Read();
            var runtimePath = state is null ? null : installation!.RuntimePath(state.Active);
            string managedVersion = "unavailable";
            if (runtimePath is not null)
            {
                try { managedVersion = Storage.Read<RuntimeManifest>(runtimePath).Version; }
                catch (Exception error) when (IsUserError(error)) { managedVersion = "unreadable"; }
            }
            installedVersions.Text = $"Launcher {version}   •   Installed runtime {managedVersion}";
            bool canUpdate = publicKey is not null && state is not null;
            bool busy = updateBusy || state?.Trial is not null || state?.Pending is not null;
            bool playing = child is { HasExited: false } || RuntimeLease.IsRunning(userRoot);
            recoveryStatus.Text = state?.Pending is { } pending
                ? (pending == state.Previous ? "Rollback staged." : "Update staged.") +
                    (playing ? " Close the game to restart." : " Restart the launcher to apply it.")
                : state?.Trial is not null ? "Verifying startup of the staged version."
                : state?.Previous is not null ? "A previous working version is available."
                : "A previous version will be available after the first successful update.";
            if (!updateBusy)
                transfer.Visible = false;
            updateStatus.Text = installation is null ? "Open the packaged ProjectQuasar.exe to enable installation and rollback."
                : publicKey is null ? "Signed updates are unavailable until a trusted release key is embedded."
                : "Play and setup are available offline.";
            automaticUpdates.Checked = settings.AutomaticChecks;
            automaticUpdates.Enabled = canUpdate && settingsError is null;
            installUpdates.Enabled = canUpdate && !busy;
            stageUpdate.Enabled = canUpdate && !busy && !playing && offer is not null;
            restart.Enabled = state?.Pending is not null && !updateBusy && !playing;
            rollback.Enabled = state?.Previous is not null && !busy && !playing;
        }
        finally { refreshing = false; }
    }

    private async Task CheckUpdates()
    {
        if (!installUpdates.Enabled)
            return;
        updateBusy = true;
        offer = null;
        ShowProgress(new("Checking for a signed release…"));
        RefreshUpdates();
        try
        {
            using var releases = new GitHubReleases();
            var candidate = await releases.Check(publicKey, closing.Token);
            var plan = installation!.Plan(candidate.Release, version, installation.RunningPair);
            offer = candidate;
            var patch = installation.SelectRuntimePatch(candidate.Release, candidate.Assets.ContainsKey);
            var download = patch is null ? "" : $" A runtime patch is available ({FormatSize(patch.Size)} of {FormatSize(candidate.Release.Runtime!.Size)}).";
            updateOffer.Text = $"Verified release. Launcher {plan.LauncherVersion} • Runtime {plan.RuntimeVersion?.ToString() ?? "unavailable"}.{download}";
        }
        catch (Exception error) when (IsUserError(error)) { updateOffer.Text = error.Message; }
        finally { updateBusy = false; RefreshUpdates(); }
    }

    private async Task StageUpdate()
    {
        if (offer is null || !stageUpdate.Enabled)
            return;
        updateBusy = true;
        applyingUpdate = true;
        ShowProgress(new("Preparing update…"));
        RefreshSetup();
        try
        {
            using var releases = new GitHubReleases();
            await releases.Stage(offer, installation!, version, publicKey, closing.Token,
                new Progress<UpdateProgress>(ShowProgress));
            offer = null;
            updateOffer.Text = "Update verified and staged. Restart to apply it.";
        }
        catch (Exception error) when (IsUserError(error)) { updateOffer.Text = error.Message; }
        finally
        {
            applyingUpdate = updateBusy = false;
            RefreshSetup();
        }
    }

    private void ShowProgress(UpdateProgress update)
    {
        if (!updateBusy)
            return;
        transfer.Visible = true;
        if (update.Received is long received && update.Total is long total && total > 0)
        {
            transfer.Style = ProgressBarStyle.Continuous;
            transfer.Value = (int)(100 * received / total);
            updateOffer.Text = $"{update.Message} · {transfer.Value}% ({FormatSize(received)} / {FormatSize(total)})";
        }
        else
        {
            transfer.Style = ProgressBarStyle.Marquee;
            updateOffer.Text = update.Message;
        }
    }

    private static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:0.#} KiB" : $"{bytes / (1024.0 * 1024):0.0} MiB";
}
