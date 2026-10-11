using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhoneRemote;

internal sealed class FakeControls : IDesktopControls
{
    internal Action<RemoteCommand>? Observe { get; set; }
    public int Commands { get; private set; }
    private int volume = 42;
    private bool muted, playing = true;
    private int appVolume = 68;
    private bool appMuted;
    public RemoteCommand? LastCommand { get; private set; }
    public Task<RemoteState> ReadAsync() => Task.FromResult(new RemoteState("DEMO-PC", new(true, volume, muted),
        new[] { new MediaState("demo-music", "Музыка", "Вечерний плейлист", "Локальная демонстрация", playing, true, true, true, true),
            new MediaState("demo-browser", "Браузер", "Видео на большом экране", "Выбери, чем управлять", false, true, true, false, false) }, null,
            new[] { new AppAudioState("demo-app", "Discord", appVolume, appMuted, true), new AppAudioState("demo-app2", "Браузер", 35, false, true) }));
    public Task<bool> ExecuteAsync(RemoteCommand command)
    {
        Commands++;
        LastCommand = command;
        Observe?.Invoke(command);
        if (command.Action == "app-volume") appVolume = command.Volume!.Value;
        if (command.Action == "app-mute") appMuted = !appMuted;
        if (command.Action == "volume") volume = command.Volume!.Value;
        if (command.Action == "mute") muted = !muted;
        if (command.Action == "pause") playing = false;
        if (command.Action == "play") playing = true;
        return Task.FromResult(true);
    }
}

internal static class SelfTest
{
    public static async Task PreviewAsync()
    {
        var trust = new TrustStore(Path.Combine(AppContext.BaseDirectory, "test-data", "preview-" + Guid.NewGuid().ToString("N")));
        var commandLog = Path.Combine(Environment.CurrentDirectory, "test-preview-commands.jsonl");
        File.WriteAllText(commandLog, "");
        var logLock = new object();
        var fake = new FakeControls { Observe = command => { lock (logLock) File.AppendAllText(commandLog, JsonSerializer.Serialize(command) + "\n"); } };
        await using var server = new RemoteServer(trust, fake, (_, _) => Task.FromResult(true), () => true);
        await server.StartAsync(0, true);
        var url = $"http://127.0.0.1:{server.Port}/#pair={trust.CreatePairingToken()}";
        await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "test-preview.json"), JsonSerializer.Serialize(new { url, fake = true }));
        await Task.Delay(TimeSpan.FromMinutes(30));
    }

    public static async Task<int> RunAsync()
    {
        var results = new List<object>();
        var folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        try
        {
            var trust = new TrustStore(folder);
            var fake = new FakeControls();
            var approved = true;
            var inputEnabled = false;
            await using var server = new RemoteServer(trust, fake, (_, _) => Task.FromResult(approved), () => inputEnabled);
            await server.StartAsync(0, true);
            var baseUrl = $"http://127.0.0.1:{server.Port}";
            var cookies = new CookieContainer();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = new Uri(baseUrl) };
            async Task<HttpResponseMessage> Post(string path, object body, string? origin = null)
            {
                var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
                message.Headers.Add("Origin", origin ?? baseUrl);
                return await client.SendAsync(message);
            }
            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                results.Add(new { name, passed = true });
            }
            string ReleaseJson(string tag = "v0.3.0", string? url = null, string? digest = null, long size = 3, bool prerelease = false) => JsonSerializer.Serialize(new
            {
                tag_name = tag, draft = false, prerelease,
                assets = new[] { new { name = AppUpdates.AssetName, browser_download_url = url ?? $"https://github.com/{AppUpdates.Repository}/releases/download/{tag}/{AppUpdates.AssetName}", size, digest = digest ?? "sha256:" + new string('a', 64) } }
            });
            void RejectRelease(string json, string name)
            {
                try { AppUpdates.ParseRelease(json); throw new Exception("Accepted invalid release: " + name); }
                catch (InvalidDataException) { Check(true, name); }
            }
            var instanceName = "test-" + Guid.NewGuid().ToString("N");
            using (var owner = new DesktopInstance(instanceName))
            {
                Check(owner.Acquire(0), "First desktop instance acquires exclusive ownership");
                var rejected = false;
                var duplicate = new Thread(() =>
                {
                    using var second = new DesktopInstance(instanceName);
                    rejected = !second.Acquire(0);
                    second.RequestOpen();
                });
                duplicate.Start(); duplicate.Join();
                Check(rejected && owner.OpenRequested() && !owner.OpenRequested(), "Second instance signals existing window without taking ownership");
            }
            using (var restarted = new DesktopInstance(instanceName))
                Check(restarted.Acquire(0), "Closing previous instance allows restart");
            var startupPath = Path.Combine(folder, "folder with spaces", "parallexity.exe");
            Check(DesktopStartup.Command(startupPath) == "\"" + Path.GetFullPath(startupPath) + "\" --startup", "Autostart quotes executable path and uses tray flag");
            Check(AppUpdates.ParseRelease(ReleaseJson()).Version == new Version(0, 3, 0), "Stable update metadata is parsed");
            RejectRelease(ReleaseJson(tag: "not-a-version"), "Invalid release version rejected");
            RejectRelease(ReleaseJson(prerelease: true), "Preview release rejected");
            RejectRelease(ReleaseJson(url: "https://attacker.invalid/update.exe"), "Foreign update host rejected");
            RejectRelease(ReleaseJson(url: "https://github.com/other/repository/releases/download/v0.3.0/" + AppUpdates.AssetName), "Foreign update repository rejected");
            RejectRelease(ReleaseJson(digest: ""), "Missing update digest rejected");
            RejectRelease(ReleaseJson(digest: "sha256:" + new string('z', 64)), "Invalid update digest rejected");
            RejectRelease(ReleaseJson(size: 400L * 1024 * 1024), "Oversized update rejected");
            var updateFile = Path.Combine(folder, "download.exe");
            await File.WriteAllBytesAsync(updateFile, new byte[] { 1, 2, 3 });
            var updateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
            var updateRelease = new AppRelease(new(0, 3, 0), "", 3, updateHash);
            await AppUpdates.VerifyFileAsync(updateFile, updateRelease);
            Check(true, "Downloaded update hash and size verified");
            try { await AppUpdates.VerifyFileAsync(updateFile, updateRelease with { Sha256 = new string('0', 64) }); throw new Exception("Accepted corrupt update"); }
            catch (InvalidDataException) { Check(true, "Corrupt update cannot be installed"); }
            try { await AppUpdates.VerifyFileAsync(updateFile, updateRelease with { Size = 4 }); throw new Exception("Accepted incomplete update"); }
            catch (InvalidDataException) { Check(true, "Incomplete update cannot be installed"); }
            var installFile = Path.Combine(folder, "parallexity.exe");
            await File.WriteAllTextAsync(installFile, "previous build");
            var dataFile = Path.Combine(folder, "devices-preserved.json");
            await File.WriteAllTextAsync(dataFile, "pairing fixture");
            AppUpdates.ReplaceExecutable(updateFile, installFile);
            Check(File.ReadAllBytes(installFile).SequenceEqual(new byte[] { 1, 2, 3 }) && File.ReadAllText(installFile + ".previous") == "previous build", "Update replaces executable and retains previous build");
            Check(File.ReadAllText(dataFile) == "pairing fixture", "Update preserves unrelated device data");
            try { AppUpdates.ReplaceExecutable(updateFile + ".missing", installFile); throw new Exception("Missing update accepted"); }
            catch (FileNotFoundException) { Check(File.ReadAllBytes(installFile).SequenceEqual(new byte[] { 1, 2, 3 }), "Failed update leaves installed executable intact"); }
            var stale = Path.Combine(folder, "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "parallexity.exe"), "stale installer");
            var unrelated = Path.Combine(folder, "updates", "keep-me");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "parallexity.exe"), "unrelated file");
            AppUpdates.CleanOldDownloads(folder);
            Check(!Directory.Exists(stale) && File.Exists(installFile + ".previous"), "Ordinary cleanup removes stale installer but retains rollback");
            using (var heldLock = new FileStream(Path.Combine(folder, "updates", "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AppUpdates.CleanOldDownloads(folder, successfulStartup: true);
                Check(File.Exists(installFile + ".previous"), "Cleanup cannot interfere with active installer");
            }
            AppUpdates.CleanOldDownloads(folder, successfulStartup: true);
            Check(!File.Exists(installFile + ".previous") && File.Exists(installFile) && File.ReadAllText(dataFile) == "pairing fixture" && File.Exists(Path.Combine(unrelated, "parallexity.exe")), "Healthy startup discards rollback and preserves app, devices and unrelated folders");
            var home = await client.GetAsync("/");
            using (var manifestResponse = await client.GetAsync("/manifest.webmanifest"))
            {
                using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
                Check(manifestResponse.Content.Headers.ContentType?.MediaType == "application/manifest+json" && manifest.RootElement.GetProperty("display").GetString() == "standalone" && manifest.RootElement.GetProperty("start_url").GetString() == "/", "Home screen manifest opens clean root URL without pairing token");
            }
            foreach (var iconSize in new[] { 180, 192, 512 })
            {
                using var iconResponse = await client.GetAsync($"/icon-{iconSize}.png");
                using var png = new MemoryStream(await iconResponse.Content.ReadAsByteArrayAsync());
                using var bitmap = new System.Drawing.Bitmap(png);
                Check(iconResponse.Content.Headers.ContentType?.MediaType == "image/png" && bitmap.Width == iconSize && bitmap.Height == iconSize, $"Home screen PNG icon {iconSize} has correct dimensions");
            }
            using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("PhoneRemote.assets.parallexity.ico")!)
            using (var icon = new System.Drawing.Icon(iconStream))
                Check(icon.Width > 0 && icon.Height > 0, "Embedded Windows logo can be loaded as an icon");
            var dragControls = new FakeControls();
            var clock = DateTimeOffset.UnixEpoch;
            var dragPermission = true;
            var lease = new DragLease(dragControls, _ => dragPermission, () => clock);
            var gesture = new string('a', 32);
            var anotherGesture = new string('b', 32);
            Check(CommandValidation.Error(new("drag-start")) is not null, "Drag requires a bounded gesture ID");
            Check(CommandValidation.Error(new("drag-move", Dx: int.MinValue, Dy: 0, Drag: gesture)) is not null, "Drag movement rejects extreme integers");
            Check(NativeInput.Build(new("drag-start", Drag: gesture)).Single().Data.Mouse.Flags == 2 && NativeInput.Build(new("drag-end", Drag: gesture)).Single().Data.Mouse.Flags == 4, "Drag emits separate mouse-down and mouse-up");
            Check(await lease.ExecuteAsync(new("drag-start", Drag: gesture), "phone1"), "Drag starts for its owner");
            Check(!await lease.ExecuteAsync(new("drag-start", Drag: anotherGesture), "phone2"), "Second phone cannot take over held mouse");
            Check(!await lease.ExecuteAsync(new("drag-end", Drag: anotherGesture), "phone1"), "Stale gesture cannot release a different drag");
            Check(!await lease.ExecuteAsync(new("click"), "phone1"), "Click cannot interfere with held drag");
            clock = clock.AddMilliseconds(500);
            Check(await lease.ExecuteAsync(new("drag-move", Dx: 0, Dy: 0, Drag: gesture), "phone1"), "Drag heartbeat renews lease without movement");
            clock = clock.AddMilliseconds(500); await lease.ExpireAsync();
            Check(dragControls.LastCommand?.Action == "drag-move", "Renewed drag survives its original expiry");
            clock = clock.AddMilliseconds(500); await lease.ExpireAsync();
            Check(dragControls.LastCommand?.Action == "drag-end", "Disconnected drag automatically releases mouse");
            Check(!await lease.ExecuteAsync(new("drag-move", Dx: 5, Dy: 5, Drag: gesture), "phone1"), "Late drag movement cannot revive expired gesture");
            Check(await lease.ExecuteAsync(new("drag-end", Drag: gesture), "phone1"), "Repeated drag-end is harmless");
            await lease.ExecuteAsync(new("drag-start", Drag: anotherGesture), "phone1");
            dragPermission = false; await lease.ExpireAsync();
            Check(dragControls.LastCommand?.Action == "drag-end", "Revoking input permission releases held mouse");
            dragPermission = true; await lease.ExecuteAsync(new("drag-start", Drag: gesture), "phone1");
            await lease.StopAsync();
            Check(dragControls.LastCommand?.Action == "drag-end" && !await lease.ExecuteAsync(new("drag-start", Drag: gesture), "phone1"), "Server shutdown releases mouse and prevents new drag");
            Check(home.IsSuccessStatusCode && (await home.Content.ReadAsStringAsync()).Contains("parallexity"), "Embedded mobile page loads");
            Check(home.Headers.Contains("Content-Security-Policy"), "Page has CSP and does not allow external scripts");
            Check((await client.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "Unpaired client cannot read PC state");
            Check((await Post("/api/command", new { action = "mute" })).StatusCode == HttpStatusCode.Unauthorized && fake.Commands == 0, "Unpaired client cannot control PC");
            var token = trust.CreatePairingToken();
            Check((await Post("/api/pair", new { token, name = "Phone" }, "https://attacker.invalid")).StatusCode == HttpStatusCode.Forbidden, "Foreign origin cannot pair");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/state"))
            {
                request.Headers.Host = "attacker.invalid:" + server.Port;
                Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "DNS rebinding host is rejected");
            }
            approved = false;
            Check((await Post("/api/pair", new { token, name = "Phone" })).StatusCode == HttpStatusCode.Forbidden && trust.Devices.Length == 0, "PC rejection grants no access");
            approved = true;
            token = trust.CreatePairingToken();
            Check((await Post("/api/pair", new { token, name = "Phone" })).IsSuccessStatusCode, "Pairing approved on host grants a session");
            Check((await Post("/api/pair", new { token, name = "Phone" })).StatusCode == HttpStatusCode.Forbidden, "Pairing token cannot be replayed");
            Check((await client.GetAsync("/api/state")).IsSuccessStatusCode, "Same-origin GET works without Origin header");
            var before = fake.Commands;
            Check((await Post("/api/command", new { action = "volume", volume = 101 })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Out-of-range volume cannot reach backend");
            Check((await Post("/api/command", new { action = "volume" })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Missing volume cannot reach backend");
            Check((await Post("/api/command", new { action = "execute", path = "cmd.exe" })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Arbitrary program execution is not exposed");
            Check((await Post("/api/command", new { action = "pause" })).StatusCode == HttpStatusCode.BadRequest, "Media commands require a selected player");
            Check((await Post("/api/command", new { action = "volume", volume = 73 })).IsSuccessStatusCode, "Valid volume reaches backend");
            var state = await client.GetFromJsonAsync<RemoteState>("/api/state");
            Check(state?.Audio.Volume == 73, "Subsequent state reflects command");
            Check((await Post("/api/command", new { action = "pause", session = "demo-music" })).IsSuccessStatusCode, "Media control reaches selected session");
            before = fake.Commands;
            Check((await Post("/api/command", new { action = "pointer", dx = 5, dy = 2 })).StatusCode == HttpStatusCode.Forbidden && fake.Commands == before, "Trusted device cannot move mouse without host opt-in");
            inputEnabled = true;
            Check((await Post("/api/command", new { action = "drag-start", drag = gesture })).IsSuccessStatusCode, "Authorized drag reaches input backend");
            Check((await Post("/api/command", new { action = "drag-move", drag = gesture, dx = 7, dy = -2 })).IsSuccessStatusCode && fake.LastCommand?.Dx == 7, "Drag movement reaches backend without losing coordinates");
            Check((await Post("/api/command", new { action = "drag-end", drag = gesture })).IsSuccessStatusCode && fake.LastCommand?.Action == "drag-end", "Drag-end releases through authenticated API");
            Check((await Post("/api/command", new { action = "pointer", dx = int.MinValue, dy = 0 })).StatusCode == HttpStatusCode.BadRequest, "Pointer validation handles extreme integers");
            Check((await Post("/api/command", new { action = "key", key = "run-shell" })).StatusCode == HttpStatusCode.BadRequest, "Keyboard whitelist rejects arbitrary keys");
            Check((await Post("/api/command", new { action = "text", text = new string('x', 501) })).StatusCode == HttpStatusCode.BadRequest, "Oversized text is rejected");
            Check((await Post("/api/command", new { action = "app-volume", volume = 40 })).StatusCode == HttpStatusCode.BadRequest, "Mixer commands require an app ID");
            Check((await Post("/api/command", new { action = "app-volume", volume = 27, app = "demo-app" })).IsSuccessStatusCode, "Per-app volume reaches backend");
            Check((await client.GetFromJsonAsync<RemoteState>("/api/state"))?.Apps?[0].Volume == 27, "Mixer state reflects per-app volume");
            Check((await Post("/api/command", new { action = "pointer", dx = 4, dy = -7 })).IsSuccessStatusCode && fake.LastCommand?.Dy == -7, "Authorized pointer command preserves direction");
            Check((await Post("/api/command", new { action = "text", text = "Привет 👋" })).IsSuccessStatusCode && fake.LastCommand?.Text == "Привет 👋", "Unicode text survives HTTP serialization");
            inputEnabled = false;
            before = fake.Commands;
            Check((await Post("/api/command", new { action = "click" })).StatusCode == HttpStatusCode.Forbidden && fake.Commands == before, "Disabling input stops commands immediately");
            Check(System.Runtime.InteropServices.Marshal.SizeOf<NativeInput.INPUT>() == 40, "Win32 INPUT structure matches x64 ABI");
            var keyEvents = NativeInput.Build(new("key", Key: "paste"));
            Check(keyEvents.Length == 4 && keyEvents[3].Data.Keyboard.VirtualKey == 0x11 && keyEvents[3].Data.Keyboard.Flags == 2, "Shortcut releases modifiers in reverse order");
            var textEvents = NativeInput.Build(new("text", Text: "Я👋\n"));
            Check(textEvents.Length == 8 && textEvents[0].Data.Keyboard.Scan == 'Я' && textEvents[6].Data.Keyboard.VirtualKey == 0x0D, "Unicode and newline produce complete input event pairs");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/command") { Content = JsonContent.Create(new { action = "mute" }) })
                Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Cookie alone is insufficient for POST commands");
            var cookie = cookies.GetCookies(new Uri(baseUrl))["pr_session"]!;
            Check(cookie.HttpOnly, "Session cookie is HttpOnly");
            Check(!File.ReadAllText(Path.Combine(folder, "devices.json")).Contains(cookie.Value), "Device store keeps token hashes, not bearer secrets");
            Check(new TrustStore(folder).IsTrusted(cookie.Value), "Pairing survives host restart");
            trust.Revoke(trust.Devices[0].Id);
            Check((await client.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "Revoked phone loses access immediately");
            Check(!new TrustStore(folder).IsTrusted(cookie.Value), "Revocation persists on disk");
            var live = NativeAudio.Read();
            Check(live.Volume is >= 0 and <= 100, "Windows audio read is valid and does not change volume");
            results.Add(new { name = "Windows audio device", available = live.Available, volume = live.Volume });
            var liveState = await new DesktopControls().ReadAsync();
            results.Add(new { name = "Windows media session read", available = liveState.MediaError is null, sessions = liveState.Media.Length, error = liveState.MediaError });
            results.Add(new { name = "Windows app audio sessions", count = liveState.Apps?.Length ?? 0 });
            await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "test-results.json"), JsonSerializer.Serialize(new { passed = true, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "test-results.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString(), checks = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
