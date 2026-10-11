using System.Security.Cryptography;
using System.Text;

namespace PhoneRemote;

public record BrowserTab(int Id, string Title, string Domain, bool Audible=false);
public record BrowserState(bool Connected, string Title, string[] Actions, BrowserTab[]? Tabs=null, int? SelectedTab=null);
public record BrowserReport(string Title, string[] Actions, string? CommandId=null, bool? Success=null,string? Generation=null, BrowserTab[]? Tabs=null, int? SelectedTab=null);
public record BrowserInstruction(string Id,string Action,string? Generation,int? TabId=null);
public record BrowserCommandRequest(string Action,int? TabId=null);
internal sealed class BrowserBridge
{
    internal static readonly string[] AllowedActions={"play","pause","seek-back","seek-forward","skip-intro","skip-ad","next-episode","fullscreen","exit-fullscreen"};
    internal static readonly string[] TabActions={"select-tab","focus-tab","refresh-tabs","detach-tab"};
    private readonly object sync=new();
    private readonly Func<DateTimeOffset> now;
    private string? code,secretHash;
    private DateTimeOffset codeExpiry,lastSeen,commandExpiry;
    private string title="";
    private string[] actions=Array.Empty<string>();
    private BrowserTab[]? tabs;
    private int? selectedTab;
    private BrowserInstruction? command;
    private TaskCompletionSource<bool>? completion;
    private readonly string? file;
    private string? generation;
    internal BrowserBridge(Func<DateTimeOffset>? clock=null,string? file=null)
    {
        now=clock??(()=>DateTimeOffset.UtcNow);this.file=file;
        if(file is not null&&File.Exists(file)){var stored=File.ReadAllText(file).Trim();if(stored.Length==64&&stored.All(Uri.IsHexDigit))secretHash=stored;}
    }
    internal string CreateCode(int port)
    {
        lock(sync){code=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));codeExpiry=now().AddMinutes(2);return port+":"+code;}
    }
    internal string? Pair(string candidate)
    {
        lock(sync)
        {
            if(code is null||now()>codeExpiry||candidate.Length!=code.Length||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate),Encoding.ASCII.GetBytes(code)))return null;
            var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var hash=Hash(secret);
            if(file is not null){Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".new",hash);File.Move(file+".new",file,true);}
            code=null;secretHash=hash;
            completion?.TrySetResult(false);completion=null;command=null;actions=Array.Empty<string>();tabs=null;selectedTab=null;title="";lastSeen=default;return secret;
        }
    }
    private static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal bool Authorized(string token) {lock(sync)return token.Length==64&&secretHash is not null&&CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(token)),Convert.FromHexString(secretHash));}
    internal BrowserState State {get{lock(sync){var connected=secretHash is not null&&now()-lastSeen<TimeSpan.FromSeconds(10);return new(connected,connected?title:"",connected?actions:Array.Empty<string>(),connected?tabs:null,connected?selectedTab:null);}}}
    internal BrowserInstruction? Report(BrowserReport report,string? token=null)
    {
        lock(sync)
        {
            if(token is not null&&!Authorized(token))return null;
            // Selecting a tab changes the page generation. Accept only an acknowledgement
            // that names the expected new selection before cancelling the old generation.
            if(command is not null&&report.CommandId==command.Id&&TabActions.Contains(command.Action))
            {
                var valid=report.Success==true&&(command.Action!="select-tab"||report.SelectedTab==command.TabId)&&
                    (command.Action!="detach-tab"||report.SelectedTab is null);
                completion?.TrySetResult(valid);command=null;completion=null;
            }
            if(report.Generation!=generation){completion?.TrySetResult(false);command=null;completion=null;generation=report.Generation;}
            lastSeen=now();title=report.Title;actions=report.Actions.Distinct().Where(AllowedActions.Contains).ToArray();tabs=report.Tabs;selectedTab=report.SelectedTab;
            if(command is not null&&report.CommandId==command.Id){completion?.TrySetResult(report.Success==true);command=null;completion=null;}
            if(command is not null&&now()>commandExpiry){completion?.TrySetResult(false);command=null;completion=null;}
            return command;
        }
    }
    internal async Task<bool> ExecuteAsync(string action,int? tabId=null)
    {
        Task<bool> task;
        var management=TabActions.Contains(action);
        var timeout=TimeSpan.FromSeconds(management?8:4);
        lock(sync)
        {
            if(!State.Connected||command is not null)return false;
            if(management)
            {
                if(tabs is null||((action is "select-tab" or "focus-tab")&&!tabs.Any(tab=>tab.Id==tabId))||
                    ((action is "refresh-tabs" or "detach-tab")&&tabId is not null)||(action=="detach-tab"&&selectedTab is null))return false;
            }
            else if(tabId is not null||!actions.Contains(action))return false;
            command=new(Guid.NewGuid().ToString("N"),action,generation,tabId);commandExpiry=now()+timeout;
            completion=new(TaskCreationOptions.RunContinuationsAsynchronously);task=completion.Task;
        }
        try{return await task.WaitAsync(timeout);}
        catch(TimeoutException){lock(sync){if(completion?.Task==task){command=null;completion=null;}}return false;}
    }
    internal void Stop(){lock(sync){completion?.TrySetResult(false);completion=null;command=null;actions=Array.Empty<string>();tabs=null;selectedTab=null;lastSeen=default;}}
    internal void Disconnect(){lock(sync){if(file is not null)File.WriteAllText(file,"");code=null;secretHash=null;Stop();}}
}
