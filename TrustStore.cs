using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneRemote;

public record TrustedDevice(string Id, string TokenHash, string Name, string Address, DateTimeOffset Added, DateTimeOffset Expires);

public sealed class TrustStore
{
    private readonly object sync = new();
    private readonly string file;
    private List<TrustedDevice> devices = new();
    private string? pairingToken;
    private DateTimeOffset pairingExpires;
    public event Action? Changed;

    public TrustStore(string folder)
    {
        Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "devices.json");
        if (File.Exists(file))
        {
            try { devices = JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(file)) ?? new(); }
            catch (JsonException) { devices = new(); }
        }
        devices.RemoveAll(d => d.Expires <= DateTimeOffset.UtcNow);
    }

    public string CreatePairingToken()
    {
        lock (sync)
        {
            pairingToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            pairingExpires = DateTimeOffset.UtcNow.AddMinutes(2);
            return pairingToken;
        }
    }

    public bool ConsumePairingToken(string token)
    {
        lock (sync)
        {
            if (pairingToken is null || pairingExpires <= DateTimeOffset.UtcNow || !Equal(pairingToken, token)) return false;
            pairingToken = null;
            return true;
        }
    }

    public bool IsTrusted(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return false;
        var hash = Hash(token);
        lock (sync) return devices.Any(d => d.Expires > DateTimeOffset.UtcNow && Equal(d.TokenHash, hash));
    }

    public string Add(string name, string address)
    {
        string token;
        lock (sync)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            devices.Add(new(Guid.NewGuid().ToString("N"), Hash(token), name, address,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));
            Save();
        }
        Changed?.Invoke();
        return token;
    }

    public TrustedDevice[] Devices { get { lock (sync) return devices.ToArray(); } }
    public void Revoke(string id)
    {
        lock (sync) { devices.RemoveAll(d => d.Id == id); Save(); }
        Changed?.Invoke();
    }
    private void Save()
    {
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(devices));
        File.Move(file + ".tmp", file, true);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
