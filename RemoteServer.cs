using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace PhoneRemote;

public record PairRequest(string Token, string Name);
public sealed class RemoteServer : IAsyncDisposable
{
    private readonly TrustStore trust;
    private readonly IDesktopControls controls;
    private readonly Func<string, string, Task<bool>> approve;
    private readonly Func<bool> allowInput;
    private readonly Dictionary<string, Queue<DateTimeOffset>> attempts = new();
    private readonly object rateLock = new();
    private WebApplication? app;
    private DragLease? drag;
    private System.Threading.Timer? dragTimer;
    private PcTimer? pcTimer;
    private readonly BrowserBridge browser;
    private readonly ViewingMode viewing;
    private readonly ComputerHub computer;
    internal ComputerHub ComputerForSettings=>computer;
    private readonly Func<DateTimeOffset>? timerClock;
    internal Task TickTimerAsync() => pcTimer?.TickAsync() ?? Task.CompletedTask;
    public bool ViewingEnabled => viewing.Enabled;
    public bool SetViewingMode(bool enabled) => viewing.Set(enabled);
    internal string CreateBrowserCode()=>browser.CreateCode(Port);
    internal BrowserBridge BrowserForTests=>browser;
    internal void DisconnectBrowser()=>browser.Disconnect();
    public int Port { get; private set; }
    public string[] Addresses => NetworkAddresses().Select(ip => $"http://{ip}:{Port}").ToArray();

    public RemoteServer(TrustStore trust, IDesktopControls controls, Func<string, string, Task<bool>> approve, Func<bool>? allowInput = null, ViewingMode? viewingMode = null, Func<DateTimeOffset>? timerClock = null)
    { this.trust = trust; this.controls = controls; this.approve = approve; this.allowInput = allowInput ?? (() => false); browser=new(file:Path.Combine(trust.Folder,"browser-bridge.txt")); viewing=viewingMode??new(); this.timerClock=timerClock;computer=new(trust.Folder,controls is not DesktopControls); }

    public async Task StartAsync(int port = 8765, bool loopbackOnly = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders(); // Pairing URLs and device addresses must not enter access logs.
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = 4096;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.MaxConcurrentConnections = 32;
            server.Listen(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port);
        });
        var instance = builder.Build();
        app = instance;
        pcTimer = new PcTimer(trust.IsTrusted, async (action,session)=>
        {
            // Finishing a viewing timer restores normal idle behavior before its action.
            if(!viewing.Set(false))return false;
            return action=="pause" && session=="browser" ? await browser.ExecuteAsync(action) : await ExecuteDesktopAsync(new(action,Session:session));
        }, timerClock);
        var input = new DragLease(controls, token => allowInput() && trust.IsTrusted(token));
        drag = input;
        var checkingDrag = 0;
        dragTimer = new System.Threading.Timer(async _ =>
        {
            if (Interlocked.Exchange(ref checkingDrag, 1) != 0) return;
            try { viewing.CheckTrust(trust.IsTrusted); await input.ExpireAsync(); } catch { /* Shutdown may interrupt a Windows input call. */ }
            finally { Volatile.Write(ref checkingDrag, 0); }
        }, null, 100, 100);
        instance.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["Cache-Control"] = "no-store";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            // Reject DNS rebinding: only this machine's LAN addresses and loopback names are hosts.
            var host = context.Request.Host;
            if (host.Port != Port || !(host.Host == "localhost" || host.Host == "127.0.0.1" || NetworkAddresses().Contains(host.Host)))
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var origin = context.Request.Headers.Origin.ToString();
                if ((context.Request.Method == "POST" && origin != $"http://{host}") ||
                    (origin.Length > 0 && origin != $"http://{host}") ||
                    context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Method == "POST" && !(context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
                { context.Response.StatusCode = 415; return; }
            }
            if(context.Request.Path.StartsWithSegments("/browser"))
            {
                var limit=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if(limit is {IsReadOnly:false})limit.MaxRequestBodySize=131072;
                var origin=context.Request.Headers.Origin.ToString();
                if(!IPAddress.IsLoopback(context.Connection.RemoteIpAddress??IPAddress.Any)||host.Host is not ("127.0.0.1" or "localhost")||
                    !System.Text.RegularExpressions.Regex.IsMatch(origin,@"^chrome-extension://[a-p]{32}$")) {context.Response.StatusCode=403;return;}
                headers["Access-Control-Allow-Origin"]=origin;headers["Vary"]="Origin";
                if(context.Request.Method=="OPTIONS"){headers["Access-Control-Allow-Methods"]="POST";headers["Access-Control-Allow-Headers"]="Content-Type, Authorization";context.Response.StatusCode=204;return;}
                if(context.Request.Method!="POST"||!(context.Request.ContentType?.StartsWith("application/json",StringComparison.OrdinalIgnoreCase)??false)){context.Response.StatusCode=415;return;}
            }
            try { await next(context); }
            catch (JsonException) { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
            catch (BadHttpRequestException e) { if (!context.Response.HasStarted) context.Response.StatusCode = e.StatusCode; }
            catch (Exception)
            {
                if (!context.Response.HasStarted)
                { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "Не удалось выполнить действие на ПК." }); }
            }
        });

        instance.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        instance.MapGet("/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        instance.MapGet("/design-tokens.css", () => Asset("design-tokens.css", "text/css; charset=utf-8"));
        instance.MapGet("/apple-theme.css", () => Asset("apple-theme.css", "text/css; charset=utf-8"));
        instance.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        instance.MapGet("/icon.svg", () => Asset("icon.svg", "image/svg+xml"));
        instance.MapGet("/manifest.webmanifest", () => Asset("manifest.webmanifest", "application/manifest+json"));
        foreach (var size in new[] { 180, 192, 512 })
        {
            var name = $"icon-{size}.png";
            instance.MapGet("/" + name, () => Asset(name, "image/png"));
        }
        instance.MapPost("/api/pair", async (HttpContext context) =>
        {
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!AllowPairAttempt(address)) return Results.Json(new { error = "Слишком много попыток. Подожди минуту." }, statusCode: 429);
            var request = await context.Request.ReadFromJsonAsync<PairRequest>();
            if (request is null || request.Token is null || request.Token.Length > 64 || request.Name is null || request.Name.Length > 40)
                return Results.BadRequest(new { error = "Некорректный запрос подключения." });
            if (!trust.ConsumePairingToken(request.Token)) return Results.Json(new { error = "QR-код устарел или уже использован. Создай новый на ПК." }, statusCode: 403);
            var name = new string(request.Name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) name = "Телефон";
            if (!await approve(name, address)) return Results.Json(new { error = "Подключение не подтверждено на ПК. Создай новый QR-код и попробуй снова." }, statusCode: 403);
            var token = trust.Add(name, address);
            context.Response.Cookies.Append("pr_session", token, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(30), IsEssential = true });
            return Results.Ok(new { paired = true });
        });
        instance.MapGet("/api/state", async (HttpContext context) =>
        {
            if (!trust.IsTrusted(context.Request.Cookies["pr_session"])) return Results.Unauthorized();
            viewing.CheckTrust(trust.IsTrusted);
            return Results.Ok((await controls.ReadAsync()) with { InputEnabled = allowInput(), Timer=pcTimer.State, Browser=browser.State, ViewingMode=viewing.Enabled,ComputerPanel=computer.Read() });
        });
        instance.MapPost("/api/computer",async(HttpContext context)=>
        {
            if(!trust.IsTrusted(context.Request.Cookies["pr_session"]))return Results.Unauthorized();
            var request=await context.Request.ReadFromJsonAsync<ComputerRequest>();
            if(request is null||request.Action is not ("focus" or "launch")||string.IsNullOrEmpty(request.Id)||request.Id.Length>64)return Results.BadRequest();
            return await Task.Run(()=>computer.Execute(request))?Results.Ok(new{done=true}):Results.Json(new{error=request.Action=="focus"?"Окно закрыто или Windows не разрешила вывести его вперёд. Проверь ПК и повтори.":"Не удалось запустить ярлык. Проверь избранное в приложении ПК или подожди пару секунд."},statusCode:409);
        });
        instance.MapPost("/api/viewing",async(HttpContext context)=>
        {
            var token=context.Request.Cookies["pr_session"];
            if(!trust.IsTrusted(token))return Results.Unauthorized();
            var request=await context.Request.ReadFromJsonAsync<ViewingModeRequest>();
            if(request?.Enabled is not bool enabled)return Results.BadRequest();
            return viewing.Set(enabled,token)?Results.Ok(new{done=true}):Results.Json(new{error="Windows не разрешила режим просмотра. Попробуй ещё раз."},statusCode:409);
        });
        instance.MapPost("/api/timer",async(HttpContext context)=>
        {
            var token=context.Request.Cookies["pr_session"];
            if(!trust.IsTrusted(token))return Results.Unauthorized();
            var request=await context.Request.ReadFromJsonAsync<PcTimerRequest>();
            if(request is null)return Results.BadRequest();
            if(request.Action=="cancel"){pcTimer.Cancel();return Results.Ok(new{done=true});}
            return pcTimer.Start(request,token!)?Results.Ok(new{done=true}):Results.BadRequest(new{error="Выбери таймер от 1 до 240 минут и плеер для паузы."});
        });
        instance.MapPost("/api/browser",async(HttpContext context)=>
        {
            if(!trust.IsTrusted(context.Request.Cookies["pr_session"]))return Results.Unauthorized();
            var request=await context.Request.ReadFromJsonAsync<BrowserCommandRequest>();
            if(request is null||!(BrowserBridge.AllowedActions.Contains(request.Action)||BrowserBridge.TabActions.Contains(request.Action)))return Results.BadRequest();
            return await browser.ExecuteAsync(request.Action,request.TabId,request.Volume)?Results.Ok(new{done=true}):Results.Json(new{error="Вкладка закрыта или браузер не ответил. Обнови список и проверь расширение."},statusCode:409);
        });
        instance.MapPost("/browser/pair",async(HttpContext context)=>
        {
            var request=await context.Request.ReadFromJsonAsync<PairRequest>();
            var token=request?.Token is {Length:32} code?browser.Pair(code):null;
            return token is null?Results.Json(new{error="Код истёк. Создай новый в приложении ПК."},statusCode:403):Results.Ok(new{token});
        });
        instance.MapPost("/browser/poll",async(HttpContext context)=>
        {
            var auth=context.Request.Headers.Authorization.ToString();
            if(!auth.StartsWith("Bearer ")||!browser.Authorized(auth[7..]))return Results.Unauthorized();
            var report=await context.Request.ReadFromJsonAsync<BrowserReport>();
            if(report?.Title is null||report.Title.Length>140||report.Actions is null||report.Actions.Length>12||report.Actions.Any(a=>!BrowserBridge.AllowedActions.Contains(a))||report.Generation is not {Length:32}||!report.Generation.All(Uri.IsHexDigit))return Results.BadRequest();
            if(report.Tabs is { } tabs&&(tabs.Length>200||tabs.Any(t=>t is null||t.Id<=0||t.Title is null||t.Title.Length>100||t.Domain is null||t.Domain.Length>120||t.Domain.Any(c=>char.IsControl(c)||c is '/' or ':' or '?' or '#'))||tabs.Select(t=>t.Id).Distinct().Count()!=tabs.Length||report.SelectedTab is int selected&&!tabs.Any(t=>t.Id==selected)))return Results.BadRequest();
            if(report.SelectedTab is <=0||report.ResumeTab is <=0||report.ResumeTab is int resume&&(report.Tabs is null||!report.Tabs.Any(tab=>tab.Id==resume)))return Results.BadRequest();
            if(report.Volume is <0 or >100||(report.Volume is null)!=(report.Muted is null)||report.Volume is not null&&(!report.Actions.Contains("set-volume")||report.SelectedTab is null))return Results.BadRequest();
            if(report.Diagnostics is {} d&&(d.Version is null||d.Version.Length>20||!d.Version.All(c=>char.IsAsciiDigit(c)||c=='.')||d.Status is not ("idle" or "connecting" or "ready" or "no-player" or "blocked" or "changed")||d.AttemptedTab is <=0||d.Frames is <0 or >10000||d.Responded<0||d.Blocked<0||d.Players<0||d.Responded>d.Frames||d.Blocked>d.Frames||d.Players>d.Frames))return Results.BadRequest();
            return Results.Ok(new{command=browser.Report(report,auth[7..])});
        });
        instance.MapPost("/api/command", async (HttpContext context) =>
        {
            if (!trust.IsTrusted(context.Request.Cookies["pr_session"])) return Results.Unauthorized();
            var command = await context.Request.ReadFromJsonAsync<RemoteCommand>();
            if (command is null || command.Action is null) return Results.BadRequest(new { error = "Неизвестная команда." });
            if (CommandValidation.Error(command) is { } error) return Results.BadRequest(new { error });
            if (CommandValidation.IsInput(command.Action) && !allowInput())
                return Results.Json(new { error = "Разреши мышь и клавиатуру в окне parallexity на ПК." }, statusCode: 403);
            var done = CommandValidation.IsInput(command.Action)
                ? await input.ExecuteAsync(command, context.Request.Cookies["pr_session"]!)
                : await ExecuteDesktopAsync(command);
            return done ? Results.Ok(new { done = true }) : Results.Json(new { error = "Приложение не выполнило команду. Обнови пульт или выбери другой плеер." }, statusCode: 409);
        });
        // Set before accepting connections; port=0 is used only by the local test harness.
        Port = port;
        await instance.StartAsync();
        if (port == 0) Port = new Uri(instance.Urls.First()).Port;
    }

    private Task<bool> ExecuteDesktopAsync(RemoteCommand command)
    {
        if(command.Action=="display-off"&&!viewing.Set(false))return Task.FromResult(false);
        return controls.ExecuteAsync(command);
    }

    private bool AllowPairAttempt(string address)
    {
        lock (rateLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in attempts.Keys.ToArray())
                if (attempts[key].Count == 0 || now - attempts[key].Last() > TimeSpan.FromMinutes(1)) attempts.Remove(key);
            if (!attempts.TryGetValue(address, out var queue)) attempts[address] = queue = new();
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1)) queue.Dequeue();
            if (queue.Count >= 5 || attempts.Count > 128) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private static IResult Asset(string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PhoneRemote.web." + name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Results.Bytes(memory.ToArray(), contentType);
    }
    public static string[] NetworkAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
        .Select(a => a.Address.ToString()).Distinct().ToArray();
    public async ValueTask DisposeAsync()
    {
        browser.Stop();
        viewing.Set(false);
        if(pcTimer is not null){await pcTimer.DisposeAsync();pcTimer=null;}
        if (dragTimer is not null) { await dragTimer.DisposeAsync(); dragTimer = null; }
        if (drag is not null) { await drag.StopAsync(); drag = null; }
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
    }
}
