using System.Security.Cryptography;
using System.Text;

namespace PhoneRemote;

public record BrowserState(bool Connected, string Title, string[] Actions);
public record BrowserReport(string Title, string[] Actions, string? CommandId=null, bool? Success=null,string? Generation=null);
public record BrowserInstruction(string Id,string Action,string? Generation);
internal sealed class BrowserBridge
{
    internal static readonly string[] AllowedActions={"play","pause","seek-back","seek-forward","skip-intro","skip-ad","next-episode"};
    private readonly object sync=new();
    private readonly Func<DateTimeOffset> now;
    private string? code,secretHash;
    private DateTimeOffset codeExpiry,lastSeen,commandExpiry;
    private string title="";
    private string[] actions=Array.Empty<string>();
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
            completion?.TrySetResult(false);completion=null;command=null;actions=Array.Empty<string>();title="";lastSeen=default;return secret;
        }
    }
    private static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal bool Authorized(string token) {lock(sync)return token.Length==64&&secretHash is not null&&CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(token)),Convert.FromHexString(secretHash));}
    internal BrowserState State {get{lock(sync){var connected=secretHash is not null&&now()-lastSeen<TimeSpan.FromSeconds(10);return new(connected,connected?title:"",connected?actions:Array.Empty<string>());}}}
    internal BrowserInstruction? Report(BrowserReport report,string? token=null)
    {
        lock(sync)
        {
            if(token is not null&&!Authorized(token))return null;
            if(report.Generation!=generation){completion?.TrySetResult(false);command=null;completion=null;generation=report.Generation;}
            lastSeen=now();title=report.Title;actions=report.Actions.Distinct().Where(AllowedActions.Contains).ToArray();
            if(command is not null&&report.CommandId==command.Id){completion?.TrySetResult(report.Success==true);command=null;completion=null;}
            if(command is not null&&now()>commandExpiry){completion?.TrySetResult(false);command=null;completion=null;}
            return command;
        }
    }
    internal async Task<bool> ExecuteAsync(string action)
    {
        Task<bool> task;
        lock(sync)
        {
            if(!State.Connected||!actions.Contains(action)||command is not null)return false;
            command=new(Guid.NewGuid().ToString("N"),action,generation);commandExpiry=now().AddSeconds(4);
            completion=new(TaskCreationOptions.RunContinuationsAsynchronously);task=completion.Task;
        }
        try{return await task.WaitAsync(TimeSpan.FromSeconds(4));}
        catch(TimeoutException){lock(sync){if(completion?.Task==task){command=null;completion=null;}}return false;}
    }
    internal void Stop(){lock(sync){completion?.TrySetResult(false);completion=null;command=null;actions=Array.Empty<string>();lastSeen=default;}}
    internal void Disconnect(){lock(sync){if(file is not null)File.WriteAllText(file,"");code=null;secretHash=null;Stop();}}
}
