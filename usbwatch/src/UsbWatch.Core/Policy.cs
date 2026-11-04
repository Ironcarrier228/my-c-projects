using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsbWatch.Core;

public enum SessionPhase { Active, Locked, NoUser }

public class UsbPolicy
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("defaultAction")]
    public string DefaultAction { get; set; } = "quarantine";

    [JsonPropertyName("rules")]
    public List<PolicyRule> Rules { get; set; } = new();

    [JsonPropertyName("temporary")]
    public List<TempGrant> Temporary { get; set; } = new();
}

public class PolicyRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "allow"; // allow | deny | readOnly

    [JsonPropertyName("match")]
    public string Match { get; set; } = "fingerprint"; // fingerprint | vidpid | class

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("denyComposite")]
    public bool DenyComposite { get; set; }

    [JsonPropertyName("denyCompositeWithHid")]
    public bool DenyCompositeWithHid { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("phases")]
    public List<string> Phases { get; set; } = new() { "active", "locked", "nouser" };
}

public class TempGrant
{
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("grantedAtUtc")]
    public DateTime GrantedAtUtc { get; set; }

    [JsonPropertyName("ttlMinutes")]
    public int TtlMinutes { get; set; } = 60;

    [JsonPropertyName("grantedBy")]
    public string GrantedBy { get; set; } = "ui";

    public bool IsExpired(DateTime nowUtc) => nowUtc > GrantedAtUtc + TimeSpan.FromMinutes(TtlMinutes);
}


public static class PolicyStore
{
    public static UsbPolicy Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<UsbPolicy>(File.ReadAllText(path)) ?? new UsbPolicy()
        : new UsbPolicy();

    public static void Save(string path, UsbPolicy p) =>
        File.WriteAllText(path, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
}
