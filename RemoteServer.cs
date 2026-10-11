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
    public int Port { get; private set; }
    public string[] Addresses => NetworkAddresses().Select(ip => $"http://{ip}:{Port}").ToArray();

    public RemoteServer(TrustStore trust, IDesktopControls controls, Func<string, string, Task<bool>> approve, Func<bool>? allowInput = null)
    { this.trust = trust; this.controls = controls; this.approve = approve; this.allowInput = allowInput ?? (() => false); }

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
        var input = new DragLease(controls, token => allowInput() && trust.IsTrusted(token));
        drag = input;
        var checkingDrag = 0;
        dragTimer = new System.Threading.Timer(async _ =>
        {
            if (Interlocked.Exchange(ref checkingDrag, 1) != 0) return;
            try { await input.ExpireAsync(); } catch { /* Shutdown may interrupt a Windows input call. */ }
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
        instance.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        instance.MapGet("/icon.svg", () => Asset("icon.svg", "image/svg+xml"));
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
            return Results.Ok((await controls.ReadAsync()) with { InputEnabled = allowInput() });
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
                : await controls.ExecuteAsync(command);
            return done ? Results.Ok(new { done = true }) : Results.Json(new { error = "Приложение не выполнило команду. Обнови пульт или выбери другой плеер." }, statusCode: 409);
        });
        // Set before accepting connections; port=0 is used only by the local test harness.
        Port = port;
        await instance.StartAsync();
        if (port == 0) Port = new Uri(instance.Urls.First()).Port;
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
        if (dragTimer is not null) { await dragTimer.DisposeAsync(); dragTimer = null; }
        if (drag is not null) { await drag.StopAsync(); drag = null; }
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
    }
}
