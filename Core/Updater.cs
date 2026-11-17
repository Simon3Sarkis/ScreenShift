using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenShift.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// Updates from the project's GitHub Releases.
///  1. Check: asks the GitHub API for the latest (non-pre-release) release.
///  2. Download: the "-win-x64.zip" asset, verified against size and GitHub's SHA-256 digest.
///  3. Stage: extracted to %TEMP%, and the exe's version must match the release.
///  4. Apply: the NEW exe is started with --apply-update; it waits for this process to exit,
///     backs up the old files, copies itself over them and starts the updated app.
///     On any failure the backup is restored and the old version is started again.
/// Nothing is installed without the user clicking "Install update".
/// </summary>
public static class Updater
{
    public const string Repo = "Simon3Sarkis/ScreenShift";
    const string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    static HttpClient? _http;
    static HttpClient Http => _http ??= CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ScreenShift-Updater/{Current}");
        return c;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static Version? ParseTag(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Returns the newer release, or null when up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version == null || version <= Current) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString() is { } ds && ds.StartsWith("sha256:"))
                sha = ds["sha256:".Length..];
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateInfo(version, tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
                url, a.GetProperty("size").GetInt64(), sha);
        }
        return null;   // newer release without a usable download
    }

    /// <summary>True if the app folder is writable (not e.g. Program Files without admin).</summary>
    public static bool CanUpdateInPlace()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Downloads, verifies and extracts the update. Returns the staging folder.</summary>
    public static async Task<string> DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ScreenShift-update-{info.Version}");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "update.zip");
        var stage = Path.Combine(root, "app");

        using (var res = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                if (total > info.AssetSize + 1024) throw new InvalidDataException("The download is larger than expected.");
                if (info.AssetSize > 0) progress?.Report((double)total / info.AssetSize);
            }
        }

        var fi = new FileInfo(zip);
        if (info.AssetSize > 0 && fi.Length != info.AssetSize) throw new InvalidDataException("The download is incomplete.");
        if (info.Sha256 != null)
        {
            await using var fs = File.OpenRead(zip);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download failed its checksum check.");
        }

        ZipFile.ExtractToDirectory(zip, stage);   // .NET blocks paths that escape the folder
        var exe = Path.Combine(stage, "ScreenShift.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("The update package doesn't contain ScreenShift.exe.");
        var fv = FileVersionInfo.GetVersionInfo(exe);
        var exeVersion = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
        if (exeVersion != info.Version) throw new InvalidDataException($"The package is version {exeVersion}, expected {info.Version}.");
        return stage;
    }

    /// <summary>Starts the staged exe, which replaces this installation once this process exits.</summary>
    public static void LaunchApply(string stage)
    {
        var psi = new ProcessStartInfo(Path.Combine(stage, "ScreenShift.exe")) { UseShellExecute = false, WorkingDirectory = stage };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd('\\'));
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi)?.Dispose();
        Log.Write($"Update staged in {stage}; restarting to apply.");
    }

    /// <summary>Runs inside the staged (new) exe. Never throws.</summary>
    public static void ApplyUpdate(string installDir, int oldPid)
    {
        var src = AppContext.BaseDirectory.TrimEnd('\\');
        var backup = Path.Combine(Path.GetTempPath(), $"ScreenShift-backup-{DateTime.Now:yyyyMMddHHmmss}");
        var copied = new List<string>();
        bool ok = false;
        try
        {
            try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(20_000); } catch { /* already gone */ }

            Directory.CreateDirectory(backup);
            foreach (var file in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(file);
                var dest = Path.Combine(installDir, name);
                if (File.Exists(dest)) File.Copy(dest, Path.Combine(backup, name), true);
                Retry(() => File.Copy(file, dest, true));
                copied.Add(name);
            }
            ok = true;
            Log.Write($"Updated to {Current} in {installDir}");
        }
        catch (Exception ex)
        {
            Log.Write("Update failed, rolling back: " + ex);
            foreach (var name in copied)
            {
                var b = Path.Combine(backup, name);
                try { if (File.Exists(b)) Retry(() => File.Copy(b, Path.Combine(installDir, name), true)); else File.Delete(Path.Combine(installDir, name)); }
                catch (Exception rex) { Log.Write("Rollback of " + name + " failed: " + rex.Message); }
            }
        }

        try
        {
            var psi = new ProcessStartInfo(Path.Combine(installDir, "ScreenShift.exe")) { UseShellExecute = false, WorkingDirectory = installDir };
            psi.ArgumentList.Add(ok ? "--updated" : "--update-failed");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Write("Could not restart after update: " + ex.Message); }
    }

    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(250); }
        }
    }

    /// <summary>Removes leftover staging/backup folders from earlier updates.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var running = AppContext.BaseDirectory;
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "ScreenShift-*"))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("ScreenShift-update-") && !name.StartsWith("ScreenShift-backup-")) continue;
                if (running.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetLastWriteTime(dir) > DateTime.Now.AddMinutes(-10)) continue;
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    public static void OpenInBrowser(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
}
