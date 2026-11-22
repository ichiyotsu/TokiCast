using System.Text.Json;
using Windows.Storage;

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

/// <summary>设置读写：ApplicationData.LocalSettings，键值对持久化。</summary>
public static class AppSettings
{
    private static TokiCastSettings? _current;
    public static TokiCastSettings Current => _current ??= Load();

    public static TokiCastSettings Load()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        var s = new TokiCastSettings();
        string Get(string key, string fallback) => values.TryGetValue(key, out var v) && v is string str ? str : fallback;
        bool GetBool(string key, bool fallback) => values.TryGetValue(key, out var v) && v is bool b ? b : fallback;
        int GetInt(string key, int fallback) => values.TryGetValue(key, out var v) && v is int i ? i : fallback;
        s.UserName = Get("UserName", s.UserName);
        s.ForegroundColor = Get("ForegroundColor", s.ForegroundColor);
        s.BackgroundColor = Get("BackgroundColor", s.BackgroundColor);
        s.Language = Get("Language", s.Language);
        s.CameraDevice = Get("CameraDevice", s.CameraDevice);
        s.MicrophoneDevice = Get("MicrophoneDevice", s.MicrophoneDevice);
        s.MicOnConnect = GetBool("MicOnConnect", s.MicOnConnect);
        s.RecordRemoteScreen = GetBool("RecordRemoteScreen", s.RecordRemoteScreen);
        s.Resolution = Get("Resolution", s.Resolution);
        s.FrameRate = GetInt("FrameRate", s.FrameRate);
        s.BitrateMbps = GetInt("BitrateMbps", s.BitrateMbps);
        s.BonjourEnabled = GetBool("BonjourEnabled", s.BonjourEnabled);
        s.BonjourUrl = Get("BonjourUrl", s.BonjourUrl);
        s.DebugLog = GetBool("DebugLog", s.DebugLog);
        s.E2eeEnabled = GetBool("E2eeEnabled", s.E2eeEnabled);
        s.SFrameEnabled = GetBool("SFrameEnabled", s.SFrameEnabled);
        s.StunTurnJson = Get("StunTurnJson", s.StunTurnJson);
        _current = s;
        return s;
    }

    public static void Save()
    {
        var s = Current;
        var values = ApplicationData.Current.LocalSettings.Values;
        values["UserName"] = s.UserName;
        values["ForegroundColor"] = s.ForegroundColor;
        values["BackgroundColor"] = s.BackgroundColor;
        values["Language"] = s.Language;
        values["CameraDevice"] = s.CameraDevice;
        values["MicrophoneDevice"] = s.MicrophoneDevice;
        values["MicOnConnect"] = s.MicOnConnect;
        values["RecordRemoteScreen"] = s.RecordRemoteScreen;
        values["Resolution"] = s.Resolution;
        values["FrameRate"] = s.FrameRate;
        values["BitrateMbps"] = s.BitrateMbps;
        values["BonjourEnabled"] = s.BonjourEnabled;
        values["BonjourUrl"] = s.BonjourUrl;
        values["DebugLog"] = s.DebugLog;
        values["E2eeEnabled"] = s.E2eeEnabled;
        values["SFrameEnabled"] = s.SFrameEnabled;
        values["StunTurnJson"] = s.StunTurnJson;
    }
}

/// <summary>最近会话持久化：JSON 文件存本地文件夹，最多 10 条；点一条就能一键重新加入。</summary>
public static class RecentSessionStore
{
    private const string FileName = "recent-sessions.json";
    private const int MaxItems = 10;

    public static async Task<IReadOnlyList<RecentSessionItem>> LoadAsync()
    {
        try
        {
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) as StorageFile;
            if (file is null) return Array.Empty<RecentSessionItem>();
            var json = await FileIO.ReadTextAsync(file);
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
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(list));
        }
        catch
        {
            // 本地保存失败不打断分享流程。
        }
    }
}

public sealed record RecentSessionItem(string RoomId, string InviteLink, string SourceName, string CreatedAt);
