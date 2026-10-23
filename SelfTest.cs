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
    public static async Task PreviewAsync(int port = 0,bool browserDemo=false)
    {
        var trust = new TrustStore(Path.Combine(AppContext.BaseDirectory, "test-data", "preview-" + Guid.NewGuid().ToString("N")));
        var commandLog = Path.Combine(Environment.CurrentDirectory, "test-preview-commands.jsonl");
        File.WriteAllText(commandLog, "");
        var logLock = new object();
        var fake = new FakeControls { Observe = command => { lock (logLock) File.AppendAllText(commandLog, JsonSerializer.Serialize(command) + "\n"); } };
        await using var server = new RemoteServer(trust, fake, (_, _) => Task.FromResult(true), () => true, new ViewingMode(_=>true));
        await server.StartAsync(port, true);
        server.ComputerForSettings.Favorites.Add("Музыка","link","https://example.test/music");
        server.ComputerForSettings.Favorites.Add("Сериал","link","https://example.test/series");
        using var browserTicker=browserDemo?StartBrowserDemo(server):null;
        var url = $"http://127.0.0.1:{server.Port}/#pair={trust.CreatePairingToken()}";
        await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "test-preview.json"), JsonSerializer.Serialize(new { url, fake = true }));
        await Task.Delay(TimeSpan.FromMinutes(30));
    }

    private static System.Threading.Timer StartBrowserDemo(RemoteServer server)
    {
        var browser=server.BrowserForTests;
        browser.Pair(server.CreateBrowserCode().Split(':')[1]);
        var tabs=new[]{new BrowserTab(41,"Вечерний фильм · тестовая вкладка","video.test"),new BrowserTab(42,"Музыка · тестовая вкладка","music.test",true),new BrowserTab(43,"Страница без плеера · тестовая вкладка","example.test")};
        int? selected=41,lastTab=41;var videoVolume=65;var videoMuted=false;var generation=Guid.NewGuid().ToString("N");string? ack=null;bool? success=null;var playing=true;var fullscreen=false;
        void Tick()
        {
            var title=tabs.FirstOrDefault(tab=>tab.Id==selected)?.Title??"";
            var actions=selected is 41 or 42?new[]{playing?"pause":"play","seek-back","seek-forward",fullscreen?"exit-fullscreen":"fullscreen","set-volume",videoMuted?"unmute-player":"mute-player"}:Array.Empty<string>();
            var command=browser.Report(new(title,actions,ack,success,generation,tabs,selected,lastTab,selected is 41 or 42?videoVolume:null,selected is 41 or 42?videoMuted:null));ack=null;success=null;
            if(command is null)return;
            ack=command.Id;success=true;
            if(command.Action=="select-tab"){selected=command.TabId;if(selected is 41 or 42)lastTab=selected;generation=Guid.NewGuid().ToString("N");}
            if(command.Action=="detach-tab"){selected=null;generation=Guid.NewGuid().ToString("N");}
            if(command.Action=="play")playing=true;if(command.Action=="pause")playing=false;
            if(command.Action=="set-volume")videoVolume=command.Volume!.Value;
            if(command.Action=="mute-player")videoMuted=true;if(command.Action=="unmute-player")videoMuted=false;
            if(command.Action=="fullscreen")fullscreen=true;if(command.Action=="exit-fullscreen")fullscreen=false;
        }
        Tick();return new System.Threading.Timer(_=>Tick(),null,1000,1000);
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
            var serverClock=DateTimeOffset.UtcNow;
            await using var server = new RemoteServer(trust, fake, (_, _) => Task.FromResult(approved), () => inputEnabled, new ViewingMode(_=>true), ()=>serverClock);
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
            Check(!FavoriteStore.ValidTarget("link","javascript:alert(1)")&&!FavoriteStore.ValidTarget("link","file:///C:/Windows/win.ini")&&!FavoriteStore.ValidTarget("link","https://user:password@example.test/"),"Favorites reject script, file and credential-bearing links");
            Check(!FavoriteStore.ValidTarget("app",@"\\server\share\app.exe")&&!FavoriteStore.ValidTarget("app","app.exe")&&!FavoriteStore.ValidTarget("app",@"C:\test.cmd"),"Favorites accept only local absolute EXE targets");
            var savedFavorites=server.ComputerForSettings.Favorites;
            savedFavorites.Add("Мой сайт","link","https://example.test/watch?token=PRIVATE-FAVORITE");
            var savedId=savedFavorites.Items.Single().Id;
            Check(new FavoriteStore(folder).Items.Single().Id==savedId,"Favorites survive reopening their local store");
            var nativeWindows=new WindowCatalog().Read();
            Check(nativeWindows.All(window=>window.Id.Length==24&&window.Title.Length<=256)&&!new WindowCatalog().Focus("missing"),"Native window discovery returns bounded opaque identifiers and rejects unpublished windows");
            var publicComputer=JsonSerializer.Serialize(server.ComputerForSettings.Read());
            Check(!publicComputer.Contains("PRIVATE-FAVORITE")&&!publicComputer.Contains("Target"),"Phone favorite catalog excludes URLs and local targets");
            Check(!server.ComputerForSettings.Execute(new("launch","unknown")),"Unknown favorite cannot launch a target");
            Check(server.ComputerForSettings.Execute(new("launch",savedId))&&!server.ComputerForSettings.Execute(new("launch",savedId)),"Demo launch acknowledges saved favorite and rejects rapid repeats without starting software");
            savedFavorites.Remove(savedId);
            Check(!server.ComputerForSettings.Execute(new("launch",savedId))&&new FavoriteStore(folder).Items.Length==0,"Deleted favorite cannot be launched and removal persists");
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
            var extensionResources=typeof(MainForm).Assembly.GetManifestResourceNames();
            foreach(var file in new[]{"manifest.json","background.js","content.js","player.js","popup.html","popup.js","popup.css","icon.png"})
                Check(extensionResources.Contains("PhoneRemote.browser-extension."+file),"Bundled extension includes "+file);
            Check(DesktopStartup.Command(startupPath) == "\"" + Path.GetFullPath(startupPath) + "\" --startup", "Autostart quotes executable path and uses tray flag");
            Check(AppUpdates.ParseRelease(ReleaseJson()).Version == new Version(0, 3, 0), "Stable update metadata is parsed");
            var powerCalls=new List<bool>();
            using(var viewingFixture=new ViewingMode(value=>{powerCalls.Add(value);return true;}))
            {
                Check(!viewingFixture.Enabled,"Viewing mode starts disabled");
                Check(viewingFixture.Set(true,"owner")&&viewingFixture.Set(true,"owner")&&powerCalls.Count==1,"Repeated enable does not duplicate power requests");
                viewingFixture.CheckTrust(_=>true);Check(viewingFixture.Enabled,"Trusted device remains active without phone heartbeat");
                viewingFixture.CheckTrust(_=>false);Check(!viewingFixture.Enabled&&powerCalls.SequenceEqual(new[]{true,false}),"Revocation releases viewing requests");
                viewingFixture.Set(true);viewingFixture.Dispose();Check(!viewingFixture.Enabled&&!viewingFixture.Set(true),"Disposal releases requests and prevents reactivation");
            }
            using(var failingViewing=new ViewingMode(_=>false))Check(!failingViewing.Set(true)&&!failingViewing.Enabled,"Failed power request is not reported as enabled");
            Check(System.Runtime.InteropServices.Marshal.SizeOf<ViewingMode.ReasonContext>()==32,"Power request reason context matches x64 ABI");
            var timerClock=DateTimeOffset.UtcNow;var timerCalls=0;var timerTrusted=true;
            await using(var timerFixture=new PcTimer(_=>timerTrusted,(_,_)=>{timerCalls++;return Task.FromResult(true);},()=>timerClock))
            {
                Check(!timerFixture.Start(new(0,"display-off"),"phone")&&!timerFixture.Start(new(15,"pause"),"phone"),"Timer rejects invalid duration and missing player");
                Check(timerFixture.Start(new(1,"display-off"),"phone")&&timerFixture.State.Remaining==60,"PC timer starts independently of phone polling");
                await timerFixture.TickAsync();Check(timerCalls==0,"Timer does not execute early");
                timerClock=timerClock.AddMinutes(1);await timerFixture.TickAsync();await timerFixture.TickAsync();
                Check(timerCalls==1&&timerFixture.State.Result=="Таймер выполнен.","Timer fires exactly once at deadline");
                timerFixture.Start(new(1,"display-off"),"phone");timerFixture.Cancel();timerClock=timerClock.AddMinutes(2);await timerFixture.TickAsync();
                Check(timerCalls==1,"Cancelled timer cannot fire");
                timerFixture.Start(new(1,"display-off"),"phone");timerTrusted=false;timerClock=timerClock.AddMinutes(2);await timerFixture.TickAsync();
                Check(timerCalls==1,"Revoked phone cannot execute its pending timer");
            }
            var bridgeClock=DateTimeOffset.UtcNow;var bridgeFile=Path.Combine(folder,"bridge-hash.txt");
            var bridgeFixture=new BrowserBridge(()=>bridgeClock,bridgeFile);
            var bridgeCode=bridgeFixture.CreateCode(8765).Split(':')[1];
            Check(bridgeFixture.Pair(new string('0',32)) is null,"Browser rejects invalid pairing code");
            var bridgeSecret=bridgeFixture.Pair(bridgeCode)??throw new Exception("Bridge pairing failed");
            Check(bridgeFixture.Pair(bridgeCode) is null&&bridgeFixture.Authorized(bridgeSecret),"Browser code is single-use and creates an authenticated bridge");
            Check(!File.ReadAllText(bridgeFile).Contains(bridgeSecret)&&new BrowserBridge(file:bridgeFile).Authorized(bridgeSecret),"Browser authentication persists only as a hash");
            var generation=Guid.NewGuid().ToString("N");
            bridgeFixture.Report(new("Test player",new[]{"skip-intro"},Generation:generation));
            Check(!await bridgeFixture.ExecuteAsync("skip-ad"),"Unavailable browser action cannot be queued");
            var browserWork=bridgeFixture.ExecuteAsync("skip-intro");var instruction=bridgeFixture.Report(new("Test player",new[]{"skip-intro"},Generation:generation))!;
            bridgeFixture.Report(new("Test player",new[]{"skip-intro"},instruction.Id,true,generation));
            Check(await browserWork,"Browser command completes only after matching acknowledgement");
            Check(!await bridgeFixture.ExecuteAsync("select-tab",9),"Old extension without catalog cannot select tabs");
            var fixtureTabs=new[]{new BrowserTab(7,"Video","video.test"),new BrowserTab(9,"Music","music.test",true)};
            bridgeFixture.Report(new("Video",new[]{"pause"},Generation:generation,Tabs:fixtureTabs,SelectedTab:7));
            Check(!await bridgeFixture.ExecuteAsync("select-tab",99),"Tab command must belong to latest catalog");
            var selectionWork=bridgeFixture.ExecuteAsync("select-tab",9);
            var selectionInstruction=bridgeFixture.Report(new("Video",new[]{"pause"},Generation:generation,Tabs:fixtureTabs,SelectedTab:7))!;
            Check(selectionInstruction.TabId==9,"Tab selection carries only a validated numeric tab id");
            generation=Guid.NewGuid().ToString("N");
            bridgeFixture.Report(new("Music",new[]{"pause"},selectionInstruction.Id,true,generation,fixtureTabs,9));
            Check(await selectionWork&&bridgeFixture.State.SelectedTab==9,"Selection acknowledgement survives expected page generation change");
            var wrongSelection=bridgeFixture.ExecuteAsync("select-tab",7);
            var wrongInstruction=bridgeFixture.Report(new("Music",new[]{"pause"},Generation:generation,Tabs:fixtureTabs,SelectedTab:9))!;
            bridgeFixture.Report(new("Music",new[]{"pause"},wrongInstruction.Id,true,generation,fixtureTabs,9));
            Check(!await wrongSelection,"Acknowledgement cannot claim a different selected tab");
            bridgeFixture.Report(new("Music",new[]{"pause","set-volume","mute-player"},Generation:generation,Tabs:fixtureTabs,SelectedTab:9,ResumeTab:9,Volume:65,Muted:false));
            Check(bridgeFixture.State is {Volume:65,Muted:false,ResumeTab:9},"Player audio and previous tab reach remote state");
            Check(!await bridgeFixture.ExecuteAsync("set-volume",7,32),"Player volume is pinned to selected tab");
            Check(!await bridgeFixture.ExecuteAsync("set-volume",9,101)&&!await bridgeFixture.ExecuteAsync("set-volume",9,-1)&&!await bridgeFixture.ExecuteAsync("set-volume",9),"Player volume rejects missing and out-of-range values");
            Check(!await bridgeFixture.ExecuteAsync("pause",volume:32),"Volume payload is rejected on other actions");
            Check(!await bridgeFixture.ExecuteAsync("pause",7),"Playback cannot be routed to a different selected tab");
            var pinnedPlayback=bridgeFixture.ExecuteAsync("pause",9);
            var pinnedInstruction=bridgeFixture.Report(new("Music",new[]{"pause"},Generation:generation,Tabs:fixtureTabs,SelectedTab:9))!;
            Check(pinnedInstruction is {Action:"pause",TabId:9},"Playback carries the explicit selected tab");
            bridgeFixture.Report(new("Music",new[]{"pause","set-volume"},pinnedInstruction.Id,true,generation,fixtureTabs,9));
            Check(await pinnedPlayback,"Pinned playback completes after acknowledgement");
            var browserDiagnostics=new BrowserDiagnostics("0.9.3","blocked",7,2,0,2,0);
            bridgeFixture.Report(new("Music",new[]{"pause","set-volume"},Generation:generation,Tabs:fixtureTabs,SelectedTab:9,Diagnostics:browserDiagnostics));
            Check(bridgeFixture.State.Diagnostics==browserDiagnostics,"Browser diagnostics reach trusted remote state");
            var volumeWork=bridgeFixture.ExecuteAsync("set-volume",9,32);
            var volumeInstruction=bridgeFixture.Report(new("Music",new[]{"set-volume"},Generation:generation,Tabs:fixtureTabs,SelectedTab:9))!;
            Check(volumeInstruction is {TabId:9,Volume:32},"Volume command preserves intended tab and level");
            bridgeFixture.Report(new("Music",new[]{"pause","set-volume"},volumeInstruction.Id,true,generation,fixtureTabs,9,9,32,false));
            Check(await volumeWork&&bridgeFixture.State.Volume==32,"Volume completes after confirmed player acknowledgement");
            var staleWork=bridgeFixture.ExecuteAsync("pause");
            bridgeFixture.Report(new("New page",new[]{"skip-intro"},Generation:Guid.NewGuid().ToString("N")));
            Check(!await staleWork,"Navigation cancels commands belonging to previous page");
            bridgeClock=bridgeClock.AddSeconds(11);Check(!bridgeFixture.State.Connected,"Browser capabilities expire when heartbeat stops");
            var expiredCode=bridgeFixture.CreateCode(8765).Split(':')[1];bridgeClock=bridgeClock.AddMinutes(3);Check(bridgeFixture.Pair(expiredCode) is null,"Browser pairing code expires");
            bridgeFixture.Disconnect();Check(!bridgeFixture.Authorized(bridgeSecret)&&!new BrowserBridge(file:bridgeFile).Authorized(bridgeSecret),"Disconnect revokes browser access persistently");
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
            Check((await Post("/api/viewing",new{enabled=true})).StatusCode==HttpStatusCode.Unauthorized&&!server.ViewingEnabled,"Unpaired device cannot enable viewing mode");
            Check((await Post("/api/command", new { action = "mute" })).StatusCode == HttpStatusCode.Unauthorized && fake.Commands == 0, "Unpaired client cannot control PC");
            Check((await Post("/api/command", new { action = "display-off" })).StatusCode == HttpStatusCode.Unauthorized && fake.Commands == 0, "Unpaired client cannot turn off display");
            Check((await Post("/api/timer",new{minutes=15,action="display-off"})).StatusCode==HttpStatusCode.Unauthorized,"Unpaired phone cannot start timer");
            Check((await Post("/api/browser",new{action="skip-ad"})).StatusCode==HttpStatusCode.Unauthorized,"Unpaired phone cannot control browser");
            Check((await Post("/api/computer",new{action="focus",id="demo-chrome"})).StatusCode==HttpStatusCode.Unauthorized,"Unpaired phone cannot switch windows");
            Check((await Post("/browser/pair",new{token=new string('0',32),name="Browser"})).StatusCode==HttpStatusCode.Forbidden,"Website origin cannot pair browser bridge");
            var extensionOrigin="chrome-extension://"+new string('a',32);
            var apiBridgeCode=server.CreateBrowserCode().Split(':')[1];
            var bridgePairResponse=await Post("/browser/pair",new{token=apiBridgeCode,name="Browser"},extensionOrigin);
            using var bridgePairJson=JsonDocument.Parse(await bridgePairResponse.Content.ReadAsStringAsync());
            var apiBridgeToken=bridgePairJson.RootElement.GetProperty("token").GetString();
            using(var bridgeReportRequest=new HttpRequestMessage(HttpMethod.Post,"/browser/poll"){Content=JsonContent.Create(new{title="Test player",actions=new[]{"skip-intro"},generation=Guid.NewGuid().ToString("N")})})
            {
                bridgeReportRequest.Headers.Add("Origin",extensionOrigin);bridgeReportRequest.Headers.Add("Authorization","Bearer "+apiBridgeToken);
                Check((await client.SendAsync(bridgeReportRequest)).IsSuccessStatusCode,"Authenticated loopback extension can report player capabilities");
            }
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
            Check((await Post("/api/computer",new{action="focus",id="demo-chrome"})).IsSuccessStatusCode,"Trusted phone can request a demo window without enabling pointer input");
            Check((await Post("/api/computer",new{action="focus",id="closed-window"})).StatusCode==HttpStatusCode.Conflict,"Missing window returns an explicit failure");
            Check((await Post("/api/computer",new{action="launch",id="C:\\Windows\\notepad.exe"})).StatusCode==HttpStatusCode.Conflict,"Phone cannot launch a raw executable path");
            Check((await Post("/api/computer",new{action="add",id="example"})).StatusCode==HttpStatusCode.BadRequest,"Phone cannot create launcher entries through command API");
            Check((await Post("/api/computer",new{action="focus",id="demo-chrome"},"https://attacker.invalid")).StatusCode==HttpStatusCode.Forbidden,"Window commands enforce same-origin policy");
            Check((await Post("/api/pair", new { token, name = "Phone" })).StatusCode == HttpStatusCode.Forbidden, "Pairing token cannot be replayed");
            async Task<HttpResponseMessage> ReportCatalog(object body)
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,"/browser/poll"){Content=JsonContent.Create(body)};
                request.Headers.Add("Origin",extensionOrigin);request.Headers.Add("Authorization","Bearer "+apiBridgeToken);
                return await client.SendAsync(request);
            }
            var catalogGeneration=Guid.NewGuid().ToString("N");
            var catalog=Enumerable.Range(1,100).Select(id=>new BrowserTab(id,new string('x',100),"video.test")).ToArray();
            Check((await ReportCatalog(new BrowserReport("Test player",new[]{"skip-intro"},Generation:catalogGeneration,Tabs:catalog,SelectedTab:7))).IsSuccessStatusCode,"Authenticated tab catalog may exceed media request body size");
            Check((await client.GetFromJsonAsync<RemoteState>("/api/state"))?.Browser is {SelectedTab:7,Tabs.Length:100},"Trusted phone receives tab catalog and selection");
            Check((await ReportCatalog(new BrowserReport("Test player",Array.Empty<string>(),Generation:catalogGeneration,Tabs:new[]{catalog[0],catalog[0]}))).StatusCode==HttpStatusCode.BadRequest,"Duplicate tab ids are rejected");
            Check((await ReportCatalog(new BrowserReport("Test player",Array.Empty<string>(),Generation:catalogGeneration,Tabs:catalog,SelectedTab:999))).StatusCode==HttpStatusCode.BadRequest,"Selected tab must belong to reported catalog");
            Check((await ReportCatalog(new BrowserReport("Test player",Array.Empty<string>(),Generation:catalogGeneration,Tabs:new[]{new BrowserTab(7,"Title","https://site.test/private?token=secret")}))).StatusCode==HttpStatusCode.BadRequest,"Tab metadata cannot contain a full URL");
            Check((await Post("/api/browser",new{action="select-tab",tabId=999})).StatusCode==HttpStatusCode.Conflict,"Phone cannot select a tab outside the current catalog");
            Check((await client.GetAsync("/api/state")).IsSuccessStatusCode, "Same-origin GET works without Origin header");
            Check((await Post("/api/viewing",new{enabled=true},"https://attacker.invalid")).StatusCode==HttpStatusCode.Forbidden&&!server.ViewingEnabled,"Foreign origin cannot enable viewing mode");
            Check((await ReportCatalog(new BrowserReport("Music",new[]{"set-volume"},Generation:catalogGeneration,Tabs:catalog,SelectedTab:7,ResumeTab:7,Volume:65,Muted:false))).IsSuccessStatusCode,"Player audio report accepts validated state");
            Check((await ReportCatalog(new BrowserReport("Music",new[]{"set-volume"},Generation:catalogGeneration,Tabs:catalog,SelectedTab:7,Volume:101,Muted:false))).StatusCode==HttpStatusCode.BadRequest,"Out-of-range reported player volume is rejected");
            Check((await ReportCatalog(new BrowserReport("Music",new[]{"set-volume"},Generation:catalogGeneration,Tabs:catalog,SelectedTab:7,Volume:65))).StatusCode==HttpStatusCode.BadRequest,"Incomplete audio report is rejected");
            Check((await ReportCatalog(new BrowserReport("Music",Array.Empty<string>(),Generation:catalogGeneration,Tabs:catalog,ResumeTab:999))).StatusCode==HttpStatusCode.BadRequest,"Resume tab must still exist in catalog");
            Check((await Post("/api/browser",new{action="set-volume",tabId=99,volume=32})).StatusCode==HttpStatusCode.Conflict,"HTTP volume cannot change another tab");
            await ReportCatalog(new BrowserReport("Test player",new[]{"skip-intro"},Generation:catalogGeneration,Tabs:catalog,SelectedTab:7));
            Check((await Post("/api/viewing",new{})).StatusCode==HttpStatusCode.BadRequest,"Viewing mode requires an explicit state");
            Check((await Post("/api/viewing",new{enabled=true})).IsSuccessStatusCode&&server.ViewingEnabled&&!inputEnabled,"Trusted phone enables viewing without keyboard access");
            Check((await client.GetFromJsonAsync<RemoteState>("/api/state"))?.ViewingMode==true,"Viewing state is visible on phone");
            await Post("/api/timer",new{minutes=1,action="display-off"});serverClock=serverClock.AddMinutes(1);await server.TickTimerAsync();
            Check(!server.ViewingEnabled&&fake.LastCommand?.Action=="display-off","Screen timer releases viewing mode before executing");
            await Post("/api/viewing",new{enabled=true});await Post("/api/timer",new{minutes=1,action="pause",session="demo-music"});serverClock=serverClock.AddMinutes(1);await server.TickTimerAsync();
            Check(!server.ViewingEnabled&&fake.LastCommand?.Action=="pause","Pause timer also restores normal idle behavior");
            Check((await Post("/api/timer",new{minutes=15,action="display-off"})).IsSuccessStatusCode,"Trusted phone can arm PC timer");
            var timerState=await client.GetFromJsonAsync<RemoteState>("/api/state");
            Check(timerState?.Timer?.Remaining is >0 and <=900&&timerState.Browser?.Actions.Contains("skip-intro")==true,"Phone state contains PC countdown and reported browser actions");
            Check((await Post("/api/timer",new{minutes=0,action="cancel"})).IsSuccessStatusCode,"Trusted phone can cancel PC timer");
            var before = fake.Commands;
            Check((await Post("/api/command", new { action = "display-off" }, "https://attacker.invalid")).StatusCode == HttpStatusCode.Forbidden && fake.Commands == before, "Foreign origin cannot turn off display");
            Check((await Post("/api/command", new { action = "sleep" })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Sleep command is not exposed as display power");
            Check((await Post("/api/command", new { action = "volume", volume = 101 })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Out-of-range volume cannot reach backend");
            Check((await Post("/api/command", new { action = "volume" })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Missing volume cannot reach backend");
            Check((await Post("/api/command", new { action = "execute", path = "cmd.exe" })).StatusCode == HttpStatusCode.BadRequest && fake.Commands == before, "Arbitrary program execution is not exposed");
            Check((await Post("/api/command", new { action = "pause" })).StatusCode == HttpStatusCode.BadRequest, "Media commands require a selected player");
            Check((await Post("/api/command", new { action = "volume", volume = 73 })).IsSuccessStatusCode, "Valid volume reaches backend");
            var state = await client.GetFromJsonAsync<RemoteState>("/api/state");
            Check(state?.Audio.Volume == 73, "Subsequent state reflects command");
            Check((await Post("/api/command", new { action = "pause", session = "demo-music" })).IsSuccessStatusCode, "Media control reaches selected session");
            await Post("/api/viewing",new{enabled=true});
            Check((await Post("/api/command", new { action = "display-off" })).IsSuccessStatusCode && fake.LastCommand?.Action == "display-off" && !inputEnabled && !server.ViewingEnabled, "Display-off releases viewing mode without enabling keyboard or mouse (fake backend)");
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
