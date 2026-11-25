using System.IO;
using System.Text.Json;

namespace P2PKiwi.WinUI3.Services;

/// <summary>应用设置：原版 p2p.kiwi 设置页（基本 / 媒体 / 高级）的全部字段，存本地，不上传。</summary>
public sealed class TokiCastSettings
{
    // 基本
    public string UserName { get; set; } = "Kiwi";
    public string ForegroundColor { get; set; } = "#ffffff";
    public string BackgroundColor { get; set; } = "#0099ff";
    public string Language { get; set; } = "zh";
    // 媒体
    public string CameraDevice { get; set; } = "系统默认";
    public string MicrophoneDevice { get; set; } = "系统默认";
    public bool MicOnConnect { get; set; } = true;
    public bool RecordRemoteScreen { get; set; }
    public string Resolution { get; set; } = "1920 × 1080";
    public int FrameRate { get; set; } = 30;
    public int BitrateMbps { get; set; } = 8;
    // 高级
    public bool BonjourEnabled { get; set; }
    public string BonjourUrl { get; set; } = "https://bonjour.p2p.kiwi";
    public bool DebugLog { get; set; }
    public bool E2eeEnabled { get; set; } = true;
    public bool SFrameEnabled { get; set; } = true;
    public string StunTurnJson { get; set; } = "{\"urls\":\"stun:stun.l.google.com:19302\"}";
}

/// <summary>
/// 设置读写：%LOCALAPPDATA%\TokiCast\settings.json。
/// 不能用 ApplicationData.LocalSettings：本应用是 Inno 安装包的无包标识（unpackaged）进程，
/// ApplicationData.Current 会以 0x80073D54（APPMODEL_ERROR_NO_PACKAGE）失败，
/// CsWinRT 把它映射成无参 InvalidOperationException——0.1.4 真机上“开始分享失败 / 设置打不开 /
/// 发消息报错”三处都是它（见 tokicast-014-audit/DIAGNOSIS-015.md）。
/// Load()/Save() 全程 try/catch、永不抛，读不到或解析失败就用默认设置。
/// </summary>
public static class AppSettings
{
    // 本地数据目录：设置 + 最近会话都放这里。
    internal static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokiCast");

    private const string SettingsFileName = "settings.json";

    // 人可读的 JSON：中文不转义、缩进，方便用户直接看/改 %LOCALAPPDATA%\TokiCast 下的文件。
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static TokiCastSettings? _current;
    public static TokiCastSettings Current => _current ??= Load();

    public static TokiCastSettings Load()
    {
        try
        {
            var path = Path.Combine(AppDataFolder, SettingsFileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (JsonSerializer.Deserialize<TokiCastSettings>(json) is { } s)
                {
                    _current = s;
                    return s;
                }
            }
        }
        catch
        {
            // 设置读不到或损坏：用默认值，不打断启动/分享/发消息。
        }
        _current = new TokiCastSettings();
        return _current;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            var path = Path.Combine(AppDataFolder, SettingsFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // 保存失败不打断设置页操作（如下次启动仍是旧值）。
        }
    }
}

/// <summary>最近会话持久化：JSON 文件存本地数据目录，最多 10 条；点一条就能一键重新加入。</summary>
public static class RecentSessionStore
{
    private const string FileName = "recent-sessions.json";
    private const int MaxItems = 10;

    public static async Task<IReadOnlyList<RecentSessionItem>> LoadAsync()
    {
        try
        {
            var path = Path.Combine(AppSettings.AppDataFolder, FileName);
            if (!File.Exists(path)) return Array.Empty<RecentSessionItem>();
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<RecentSessionItem>>(json) ?? new List<RecentSessionItem>();
        }
        catch
        {
            return Array.Empty<RecentSessionItem>();
        }
    }

    public static async Task SaveAsync(RecentSessionItem item)
    {
        try
        {
            var list = (await LoadAsync()).ToList();
            list.RemoveAll(x => x.RoomId == item.RoomId);
            list.Insert(0, item);
            if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
            Directory.CreateDirectory(AppSettings.AppDataFolder);
            var path = Path.Combine(AppSettings.AppDataFolder, FileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, AppSettings.JsonOptions));
        }
        catch
        {
            // 本地保存失败不打断分享流程。
        }
    }
}

public sealed record RecentSessionItem(string RoomId, string InviteLink, string SourceName, string CreatedAt);
