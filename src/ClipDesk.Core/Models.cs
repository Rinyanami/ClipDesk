using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipDesk.Core;

public static class Codec
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("数据格式不正确");
    public static string Id() => Guid.NewGuid().ToString("N");
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed record Entry
{
    [JsonRequired] public string Id { get; init; } = Codec.Id();
    public string Kind { get; init; } = "clip";
    public string Format { get; init; } = "text";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public string? OriginalBody { get; init; }
    public string? OriginalTitle { get; init; }
    public string? OriginalHtml { get; init; }
    public string? OriginalRtf { get; init; }
    public List<string> Assets { get; init; } = [];
    public string? SourceId { get; init; }
    public string SourceApp { get; init; } = "";
    public bool Pinned { get; init; }
    public bool Protected { get; init; }
    public bool Done { get; init; }
    public string? Due { get; init; }
    public bool Deleted { get; init; }
    public long CreatedAt { get; init; } = Codec.Now;
    public long UpdatedAt { get; init; } = Codec.Now;
    public string OcrText { get; init; } = "";
    [JsonIgnore] public string Caption
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            if (!string.IsNullOrWhiteSpace(Body))
            {
                var end = Body.IndexOf('\n');
                return (end < 0 ? Body : Body[..end]).Trim();
            }
            return Assets.Count > 0 ? "图片" : "未命名";
        }
    }
    [JsonIgnore] public string SearchText => $"{Title}\n{Body}\n{OcrText}";
}

public sealed record Change
{
    [JsonRequired] public int Protocol { get; init; } = 1;
    [JsonRequired] public string EventId { get; init; } = Codec.Id();
    [JsonRequired] public string DeviceId { get; init; } = "";
    [JsonRequired] public long Sequence { get; init; }
    [JsonRequired] public long Time { get; init; } = Codec.Now;
    [JsonRequired] public string Action { get; init; } = "save";
    [JsonRequired] public Dictionary<string, long> Clock { get; init; } = [];
    [JsonRequired] public Entry Value { get; init; } = new();
}

public sealed record Draft([property: JsonRequired] Entry Value, [property: JsonRequired] Dictionary<string, long> BaseClock, bool BodyFormatVerified = false);
public sealed record WindowLayout(double Left = 120, double Top = 120, double Width = 620, double Height = 720, bool Topmost = false, bool Open = false, int? ScreenX = null, int? ScreenY = null, double Opacity = 1, bool? KeepVisible = null);
public sealed record SyncConfig(string Endpoint = "", string Region = "cn-east-1", string Bucket = "", string Prefix = "clipdesk-personal/", bool Enabled = false, bool ReceiveClipboard = false);
public sealed record LocalSettings
{
    public string Hotkey { get; init; } = "Ctrl+Shift+V";
    public string PinnedHotkey { get; init; } = "";
    public string TodoHotkey { get; init; } = "";
    public string MemoHotkey { get; init; } = "";
    public bool Paused { get; init; }
    public int RetentionDays { get; init; } = 30;
    public int RetentionCount { get; init; } = 1000;
    public string ExcludedApps { get; init; } = "KeePass,1Password,Bitwarden";
    public Dictionary<string, WindowLayout> Windows { get; init; } = [];
    public SyncConfig Sync { get; init; } = new();
}

public static class Clocks
{
    public static bool Dominates(IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> right) => right.All(p => left.GetValueOrDefault(p.Key) >= p.Value);
    public static Dictionary<string, long> Join(IEnumerable<Change> versions)
    {
        Dictionary<string, long> result = [];
        foreach (var c in versions) foreach (var p in c.Clock) result[p.Key] = Math.Max(result.GetValueOrDefault(p.Key), p.Value);
        return result;
    }
}
