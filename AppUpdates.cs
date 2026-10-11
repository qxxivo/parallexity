using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneRemote;

internal sealed record AppRelease(Version Version, string Url, long Size, string Sha256);

internal static class AppUpdates
{
    internal const string Repository = "qxxivo/parallexity";
    internal const string AssetName = "parallexity-win-x64.exe";
    internal static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0);
    internal static string LaunchPath => Path.Combine(AppContext.BaseDirectory, "parallexity.exe");
    private const long MaxDownload = 300L * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("parallexity/" + CurrentVersion.ToString(3));
        return client;
    }

    internal static AppRelease ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version.Build < 0 ||
            release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Релиз не является стабильной версией.");
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
            var prefix = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/";
            if (!url.Equals(prefix + AssetName, StringComparison.Ordinal) || size <= 0 || size > MaxDownload ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
                !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("Не удалось проверить источник или контрольную сумму обновления.");
            return new(version, url, size, digest[7..]);
        }
        throw new InvalidDataException("В релизе нет сборки для Windows x64.");
    }

    internal static async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        using var client = CreateClient();
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Опубликованных обновлений пока нет.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bytes.Length + count > 1024 * 1024) throw new InvalidDataException("Ответ сервера слишком большой.");
            bytes.Write(buffer, 0, count);
        }
        var release = ParseRelease(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        return release.Version > new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build)) ? release : null;
    }

    internal static async Task<string> DownloadAsync(AppRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "parallexity.exe");
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                var lastProgress = -1;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > release.Size || total > MaxDownload) throw new InvalidDataException("Неверный размер обновления.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    var percent = (int)(total * 100 / release.Size);
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                if (total != release.Size) throw new InvalidDataException("Загрузка обновления не завершена.");
            }
            await VerifyFileAsync(file, release, cancellationToken);
            return file;
        }
        catch { try { File.Delete(file); Directory.Delete(directory); } catch { } throw; }
    }

    internal static async Task VerifyFileAsync(string file, AppRelease release, CancellationToken cancellationToken = default)
    {
        await using var downloaded = File.OpenRead(file);
        if (downloaded.Length != release.Size) throw new InvalidDataException("Неверный размер обновления.");
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
        if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма обновления не совпала. Установка отменена.");
    }

    internal static void ReplaceExecutable(string source, string destination)
    {
        var backup = destination + ".previous";
        var replacement = destination + ".new";
        File.Copy(source, replacement, true);
        if (File.Exists(backup)) File.Delete(backup);
        if (File.Exists(destination)) File.Move(destination, backup);
        try { File.Move(replacement, destination); }
        catch { if (File.Exists(backup)) File.Move(backup, destination); throw; }
    }

    internal static void CleanOldDownloads()
    {
        var updates = Path.Combine(AppContext.BaseDirectory, "updates");
        if (!Directory.Exists(updates)) return;
        try
        {
            using var updateLock = new FileStream(Path.Combine(updates, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var directory in Directory.GetDirectories(updates))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                try { File.Delete(Path.Combine(directory, "parallexity.exe")); Directory.Delete(directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void StartInstaller(string downloaded)
    {
        var start = new ProcessStartInfo(downloaded) { UseShellExecute = false };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(LaunchPath);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установку обновления.");
    }

    // Runs from the verified downloaded copy. No shell, administrator rights, or data migration.
    internal static void Apply(string[] args)
    {
        string? destination = null;
        try
        {
            if (args.Length != 3 || !int.TryParse(args[1], out var processId) || processId <= 0)
                throw new InvalidDataException("Неверные параметры обновления.");
            destination = Path.GetFullPath(args[2]);
            if (!Path.GetFileName(destination).Equals("parallexity.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Неверное имя приложения.");
            var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден файл обновления.");
            var expectedUpdates = Path.Combine(Path.GetDirectoryName(destination)!, "updates") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(source).StartsWith(expectedUpdates, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Обновление должно находиться в папке приложения.");
            using var updateLock = new FileStream(Path.Combine(expectedUpdates, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { using var previous = Process.GetProcessById(processId); if (!previous.WaitForExit(60000)) throw new IOException("Пульт не завершился. Повтори обновление."); }
            catch (ArgumentException) { }
            ReplaceExecutable(source, destination);
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show("Обновление не установлено.\n\n" + error.Message + "\n\nОткрой приложение вручную.", "parallexity", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
