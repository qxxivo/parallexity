namespace PhoneRemote;

public record PcTimerState(int Remaining, string? Action, string? Result);
public record PcTimerRequest(int Minutes, string Action, string? Session = null);
internal sealed class PcTimer : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly Func<string,bool> trusted;
    private readonly Func<string,string?,Task<bool>> execute;
    private readonly Func<DateTimeOffset> now;
    private readonly System.Threading.Timer? ticker;
    private (DateTimeOffset End, string Action, string? Session, string Owner)? pending;
    private string? result;
    private bool stopped;
    internal PcTimer(Func<string,bool> trusted, Func<string,string?,Task<bool>> execute, Func<DateTimeOffset>? clock = null)
    {
        this.trusted=trusted; this.execute=execute; now=clock??(()=>DateTimeOffset.UtcNow);
        if(clock is null) ticker=new System.Threading.Timer(async _=>{try{await TickAsync();}catch{}},null,1000,1000);
    }
    internal PcTimerState State { get { lock(sync) return new(pending is { } p ? Math.Max(0,(int)Math.Ceiling((p.End-now()).TotalSeconds)):0,pending?.Action,result); } }
    internal bool Start(PcTimerRequest request,string owner)
    {
        if(request.Minutes is <1 or >240 || request.Action is not ("pause" or "display-off") ||
            (request.Action=="pause" && (string.IsNullOrEmpty(request.Session)||request.Session.Length>100))) return false;
        lock(sync) { if(stopped)return false; pending=(now().AddMinutes(request.Minutes),request.Action,request.Session,owner); result=null; return true; }
    }
    internal void Cancel() { lock(sync) {pending=null;result=null;} }
    internal async Task TickAsync()
    {
        if(!await gate.WaitAsync(0))return;
        try
        {
            (DateTimeOffset End,string Action,string? Session,string Owner) work;
            lock(sync) {if(stopped||pending is not { } p||p.End>now())return;work=p;pending=null;}
            var success=false;
            if(trusted(work.Owner)) {try{success=await execute(work.Action,work.Session);}catch{}}
            lock(sync) {if(!stopped&&pending is null)result=success?"Таймер выполнен.":"Таймер не выполнился. Проверь подключение и плеер.";}
        }
        finally {gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        lock(sync){stopped=true;pending=null;}
        if(ticker is not null)await ticker.DisposeAsync();
        await gate.WaitAsync();gate.Release();
    }
}
