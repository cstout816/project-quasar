using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;

namespace Quasar;

public sealed record UpdateProgress(string Message, long? Received = null, long? Total = null);

public sealed class GitHubReleases : IDisposable
{
    private const string Api = "https://api.github.com/repos/cstout816/project-quasar/releases/latest";
    private const string Downloads = "https://github.com/cstout816/project-quasar/releases/download/";
    private readonly HttpClient http;

    public GitHubReleases() : this(new HttpClientHandler()) { }

    internal GitHubReleases(HttpMessageHandler handler)
    {
        http = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Project-Quasar-Launcher");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public sealed record Candidate(ReleaseManifest Release, byte[] Manifest, byte[] Signature,
        Dictionary<string, Uri> Assets);

    public async Task<Candidate> Check(string? publicKey, CancellationToken token)
    {
        if (publicKey is null)
            throw new InvalidOperationException("This launcher has no embedded release key. Signed GitHub updates are unavailable.");
        using var response = new MemoryStream();
        await Download(new Uri(Api), response, 1024 * 1024, token);
        using var listing = JsonDocument.Parse(response.ToArray());
        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var item in listing.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var address = item.GetProperty("browser_download_url").GetString()!;
            if (address.StartsWith(Downloads, StringComparison.Ordinal) &&
                Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host == "github.com")
                assets.Add(name, uri);
        }
        using var data = new MemoryStream();
        using var signature = new MemoryStream();
        bool hasPatches = assets.ContainsKey(UpdateVerification.PatchManifestName) ||
            assets.ContainsKey(UpdateVerification.PatchSignatureName);
        var manifestName = hasPatches ? UpdateVerification.PatchManifestName : UpdateVerification.ManifestName;
        var signatureName = hasPatches ? UpdateVerification.PatchSignatureName : UpdateVerification.SignatureName;
        await Download(Asset(assets, manifestName), data, UpdateVerification.ManifestLimit, token);
        await Download(Asset(assets, signatureName), signature, 384, token);
        var bytes = data.ToArray();
        var signed = signature.ToArray();
        var manifest = UpdateVerification.VerifyManifest(bytes, signed, publicKey);
        if (manifest.Schema != (hasPatches ? 2 : 1))
            throw new InvalidDataException("The signed release schema does not match its asset name.");
        return new(manifest, bytes, signed, assets);
    }

    public async Task<Slots> Stage(Candidate offer, Installation install, Version version,
        string? publicKey, CancellationToken token, IProgress<UpdateProgress>? progress = null)
    {
        var release = UpdateVerification.VerifyManifest(offer.Manifest, offer.Signature, publicKey);
        var work = Directory.CreateTempSubdirectory("quasar-download-").FullName;
        try
        {
            var plan = install.Plan(release, version, install.Read().Active);
            var launcher = plan.Launcher is { } app ? await Get(app.Asset, app.Size, "Downloading launcher") : null;
            var patch = plan.Runtime is null ? null : install.SelectRuntimePatch(release, offer.Assets.ContainsKey);
            if (patch is not null)
            {
                try
                {
                    var archive = await Get(patch.Asset, patch.Size, $"Downloading runtime patch from {patch.BaseVersion}");
                    progress?.Report(new("Verifying and applying the runtime patch…"));
                    return await StageFiles(launcher, null, new(patch.BaseVersion, archive));
                }
                catch (PatchBaseMismatchException)
                {
                    progress?.Report(new("Installed runtime differs from the patch base. Using the full package."));
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
                {
                    progress?.Report(new("Runtime patch unavailable. Using the full package."));
                }
            }
            var runtime = plan.Runtime is { } game ? await Get(game.Asset, game.Size, "Downloading full runtime") : null;
            progress?.Report(new("Verifying packages and staging the update…"));
            return await StageFiles(launcher, runtime);
        }
        finally { Directory.Delete(work, recursive: true); }

        Task<Slots> StageFiles(string? launcher, string? runtime, RuntimePatchDownload? patch = null) =>
            Task.Run(() => install.Stage(offer.Manifest, offer.Signature, publicKey, launcher, runtime, version, patch), token);

        async Task<string> Get(string asset, long size, string message)
        {
            var path = Path.Combine(work, asset);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            progress?.Report(new(message, 0, size));
            long lastPercent = 0;
            await Download(Asset(offer.Assets, asset), output, size, token, received =>
            {
                long percent = 100 * received / size;
                if (percent == lastPercent)
                    return;
                lastPercent = percent;
                progress?.Report(new(message, received, size));
            });
            if (output.Length != size)
                throw new InvalidDataException("Download size differs from the signed manifest.");
            return path;
        }
    }

    private static Uri Asset(Dictionary<string, Uri> assets, string name) =>
        assets.TryGetValue(name, out var uri) ? uri : throw new InvalidDataException($"Release asset missing: {name}");

    private async Task Download(Uri uri, Stream destination, long maximum, CancellationToken token,
        Action<long>? report = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.Host == "github.com")
            request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum)
            throw new InvalidDataException("Release download exceeds its size limit.");
        using var source = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            if (read > maximum - destination.Length)
                throw new InvalidDataException("Release download exceeds its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            report?.Invoke(destination.Length);
        }
    }

    public void Dispose() => http.Dispose();
}
