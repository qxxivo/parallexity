using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneRemote;

public record PcWindow(string Id, string App, string Title);
public record PcFavorite(string Id, string Name, string Kind);
public record ComputerState(PcWindow[] Windows, PcFavorite[] Favorites);
public record ComputerRequest(string Action, string Id);
internal record SavedFavorite(string Id, string Name, string Kind, string Target);

internal sealed class FavoriteStore
{
    private readonly object sync=new();
    private readonly string file;
    private List<SavedFavorite> items=new();
    internal FavoriteStore(string folder)
    {
        file=Path.Combine(folder,"favorites.json");
        if(File.Exists(file))try{items=JsonSerializer.Deserialize<List<SavedFavorite>>(File.ReadAllText(file))?.Where(Valid).Take(24).ToList()??new();}catch(JsonException){}
    }
    internal SavedFavorite[] Items {get{lock(sync)return items.ToArray();}}
    internal static bool Valid(SavedFavorite item)=>item is not null&&Guid.TryParseExact(item.Id,"N",out _)&&
        !string.IsNullOrWhiteSpace(item.Name)&&item.Name.Length<=40&&!item.Name.Any(char.IsControl)&&ValidTarget(item.Kind,item.Target);
    internal static bool ValidTarget(string kind,string target)
    {
        if(string.IsNullOrWhiteSpace(target)||target.Length>2048||target.Any(char.IsControl))return false;
        if(kind=="link")return Uri.TryCreate(target,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0;
        return kind=="app"&&Path.IsPathFullyQualified(target)&&!target.StartsWith(@"\\")&&Path.GetExtension(target).Equals(".exe",StringComparison.OrdinalIgnoreCase);
    }
    internal void Add(string name,string kind,string target)
    {
        var item=new SavedFavorite(Guid.NewGuid().ToString("N"),name.Trim(),kind,target.Trim());
        if(!Valid(item)||kind=="app"&&!File.Exists(item.Target))throw new ArgumentException("Выбери существующий EXE или ссылку http/https и название до 40 символов.");
        lock(sync){if(items.Count>=24)throw new InvalidOperationException("Можно добавить до 24 ярлыков.");var next=items.Append(item).ToList();Save(next);items=next;}
    }
    internal void Remove(string id){lock(sync){var next=items.Where(item=>item.Id!=id).ToList();Save(next);items=next;}}
    private void Save(List<SavedFavorite> next){Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".new",JsonSerializer.Serialize(next));File.Move(file+".new",file,true);}
}

internal sealed class ComputerHub
{
    internal FavoriteStore Favorites {get;}
    private readonly bool demo;
    private readonly WindowCatalog windows=new();
    private readonly object actionLock=new();
    private DateTimeOffset lastLaunch;
    internal ComputerHub(string folder,bool demo=false){Favorites=new(folder);this.demo=demo;}
    internal ComputerState Read()=>new(demo?new[]{new PcWindow("demo-chrome","Chrome","Дандадан — тестовое окно"),new PcWindow("demo-discord","Discord","Друзья"),new PcWindow("demo-files","Проводник","Загрузки")}:windows.Read(),Favorites.Items.Select(item=>new PcFavorite(item.Id,item.Name,item.Kind)).ToArray());
    internal bool Execute(ComputerRequest request)
    {
        if(request.Action=="focus")return demo?Read().Windows.Any(w=>w.Id==request.Id):windows.Focus(request.Id);
        if(request.Action!="launch")return false;
        lock(actionLock)
        {
            var item=Favorites.Items.FirstOrDefault(item=>item.Id==request.Id);
            if(item is null||!FavoriteStore.Valid(item)||DateTimeOffset.UtcNow-lastLaunch<TimeSpan.FromSeconds(2))return false;
            if(item.Kind=="app"&&!File.Exists(item.Target))return false;
            if(demo){lastLaunch=DateTimeOffset.UtcNow;return true;}
            try{Process.Start(new ProcessStartInfo(item.Target){UseShellExecute=true});lastLaunch=DateTimeOffset.UtcNow;return true;}
            catch(Exception error)when(error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException){return false;}
        }
    }
}
