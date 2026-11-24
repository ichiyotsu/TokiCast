using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using P2PKiwi.WinUI3.Models;
using P2PKiwi.WinUI3.Services;
using Windows.Graphics.Capture;

namespace P2PKiwi.WinUI3.ViewModels;

public sealed class HomeViewModel : ViewModelBase
{
    private readonly ISessionManager _sessions;
    public ObservableCollection<RecentSession> RecentSessions { get; } = new();

    public HomeViewModel(ISessionManager sessions) => _sessions = sessions;

    public async Task<string?> StartSharingAsync(bool shareAudio, bool allowControl, GraphicsCaptureItem? captureItem = null)
    {
        var session = await _sessions.CreateSessionAsync(new SessionOptions
        {
            ShareSystemAudio = shareAudio,
            AllowRemoteControl = allowControl,
            CaptureItem = captureItem,
        });
        // 来源名取系统选择器里挑的显示器/窗口名（最近会话标题用）。
        var sourceName = captureItem?.DisplayName ?? "屏幕分享";
        RecentSessions.Insert(0, new RecentSession(RecentTitle(session.RoomId, sourceName), DateTime.Now.ToString("yyyy-MM-dd HH:mm"), session.InviteLink));
        _ = RecentSessionStore.SaveAsync(new RecentSessionItem(session.RoomId, session.InviteLink, sourceName, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        return session.InviteLink;
    }

    /// <summary>最近会话标题：房间码 + 来源（不再显示 650 字符的完整邀请链接）。</summary>
    public static string RecentTitle(string roomId, string? sourceName)
        => string.IsNullOrWhiteSpace(sourceName) ? $"房间 {roomId}" : $"房间 {roomId} · {sourceName.Trim()}";

    /// <summary>主持方：粘贴加入方发回的 answer 链接，完成连接。</summary>
    public async Task CompleteSharingAsync(string answerLink)
    {
        await _sessions.CompleteSessionAsync(answerLink);
    }

    /// <summary>启动时把本地存过的最近会话读回来，点一条就能一键重新加入。</summary>
    public async Task LoadRecentAsync()
    {
        var stored = await RecentSessionStore.LoadAsync();
        RecentSessions.Clear();
        foreach (var item in stored)
            RecentSessions.Add(new RecentSession(RecentTitle(item.RoomId, item.SourceName), item.CreatedAt, item.InviteLink));
    }
}

public sealed class SessionViewModel : ViewModelBase
{
    private readonly ISessionManager _sessions;
    private readonly DispatcherQueue? _dispatcher;
    private IWebRtcService? _rtc;
    private bool _rtcWired;
    private bool _isMicMuted;
    private bool _isRecording;
    private string? _answerLink;
    private bool _localSpeaking;
    private bool _remoteSpeaking;
    private DateTime _localSpeakingUntil = DateTime.MinValue;
    private DateTime _remoteSpeakingUntil = DateTime.MinValue;
    private const double SpeakingThreshold = 0.02; // RMS 阈值 + 400ms 保持，避免指示灯闪烁
    // WireRtc 的订阅 handler 存成字段，UnwireRtc 才能逐个取消（匿名 lambda 取消不掉）。
    private Action<ChatMessage>? _chatHandler;
    private Action<string, string, double, double>? _cursorHandler;
    private Action<byte[], int, int>? _videoFrameHandler;
    private Action<double>? _localSpeakingHandler;
    private Action<double>? _remoteSpeakingHandler;
    private Action<PeerConnectionState>? _connectionHandler;
    private Action<string>? _errorHandler;
    private bool _autoRecordStarted;

    public ObservableCollection<Participant> Participants { get; } = new();
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public event Action<string, string, double, double>? RemoteCursorMoved;
    /// <summary>远端视频帧：BGRA8 + 宽 + 高，Page 负责画到 WriteableBitmap。</summary>
    public event Action<byte[], int, int>? RemoteVideoFrame;

    public bool IsMicMuted { get => _isMicMuted; private set => Set(ref _isMicMuted, value); }
    public bool IsRecording { get => _isRecording; private set => Set(ref _isRecording, value); }
    /// <summary>加入方生成的 answer 链接（主持人粘贴后接通）；接通后清空。</summary>
    public string? AnswerLink { get => _answerLink; private set => Set(ref _answerLink, value); }

    public SessionViewModel(ISessionManager sessions)
    {
        _sessions = sessions;
        // ViewModel 在 UI 线程构造，抓住 UI 调度器：WebRTC 事件在 RTP 线程上，必须切回 UI 线程才可动集合。
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    public async Task JoinAsync(string link)
    {
        var session = await _sessions.JoinSessionAsync(link);
        Participants.Clear();
        foreach (var p in session.Participants) Participants.Add(p);
        Messages.Add(new ChatMessage("系统", $"已加入房间 {session.RoomId}，正在建立点对点连接…", DateTime.Now));
        AnswerLink = session.AnswerLink;
        if (AnswerLink != null)
            Messages.Add(new ChatMessage("系统", "已生成 answer 链接：复制会话页上方的链接发回主持人，主持人粘贴并点「允许加入」后即可接通。", DateTime.Now));
        WireRtc();
    }

    /// <summary>往聊天区追加一条系统消息（加入失败、操作失败等提示用）。</summary>
    public void AddSystemMessage(string text) => Messages.Add(new ChatMessage("系统", text, DateTime.Now));

    /// <summary>主持人从主页进来时（无链接参数）：显示已创建的当前房间，而不是一张空表；无当前会话时返回 false。</summary>
    public Task<bool> LoadCurrentAsync()
    {
        var session = _sessions.Current;
        if (session is null) return Task.FromResult(false);
        Participants.Clear();
        foreach (var p in session.Participants) Participants.Add(p);
        if (session.IsHost)
        {
            Messages.Add(new ChatMessage("系统", $"房间 {session.RoomId} 已创建，邀请链接已复制到剪贴板。把链接发给对方；对方发回 answer 链接后，粘贴到主页「发起投屏」里的 answer 框并点「允许加入」。", DateTime.Now));
            AnswerLink = null;
        }
        else
        {
            Messages.Add(new ChatMessage("系统", $"房间 {session.RoomId}，正在建立点对点连接…", DateTime.Now));
            AnswerLink = session.AnswerLink;
        }
        WireRtc();
        return Task.FromResult(true);
    }

    /// <summary>把当前会话的 WebRTC 传输接到 ViewModel（只接一次；传输由 SessionManager 持有）。</summary>
    private void WireRtc()
    {
        var rtc = _sessions.Rtc;
        if (rtc == null || _rtcWired) return;
        _rtc = rtc;
        _rtcWired = true;
        IsMicMuted = rtc.IsMicMuted;
        _chatHandler = msg => RunOnUi(() => Messages.Add(msg));
        _cursorHandler = (peerId, name, x, y) => OnRemoteCursor(peerId, name, x, y);
        _videoFrameHandler = (bgra, w, h) => RemoteVideoFrame?.Invoke(bgra, w, h);
        _localSpeakingHandler = level => UpdateSpeaking(isLocal: true, level);
        _remoteSpeakingHandler = level => UpdateSpeaking(isLocal: false, level);
        _connectionHandler = state =>
        {
            RunOnUi(() =>
            {
                if (state == PeerConnectionState.Connected)
                {
                    AnswerLink = null;
                    Messages.Add(new ChatMessage("系统", "点对点已接通。", DateTime.Now));
                    if (!Participants.Any(p => p.Id != "me"))
                        Participants.Add(new Participant("peer", "对方", IsHost: false));
                    _ = AutoStartRecordingIfEnabledAsync();
                }
                else if (state == PeerConnectionState.Failed)
                {
                    Messages.Add(new ChatMessage("系统", "点对点连接失败：双方网络可能都打不通（对称 NAT 需要 TURN）。", DateTime.Now));
                }
            });
        };
        _errorHandler = msg => RunOnUi(() => Messages.Add(new ChatMessage("系统", msg, DateTime.Now)));
        rtc.ChatMessageReceived += _chatHandler;
        rtc.RemoteCursorReceived += _cursorHandler;
        rtc.RemoteVideoFrameReceived += _videoFrameHandler;
        rtc.LocalSpeakingLevelChanged += _localSpeakingHandler;
        rtc.RemoteSpeakingLevelChanged += _remoteSpeakingHandler;
        rtc.ConnectionStateChanged += _connectionHandler;
        rtc.ErrorOccurred += _errorHandler;
    }

    /// <summary>离开会话页时取消对共享传输的订阅，避免旧页面/旧 VM 被传输事件拽住泄漏；下次进页会重新 WireRtc。</summary>
    public void UnwireRtc()
    {
        var rtc = _rtc;
        if (rtc == null || !_rtcWired) return;
        _rtcWired = false;
        _rtc = null;
        if (_chatHandler != null) rtc.ChatMessageReceived -= _chatHandler;
        if (_cursorHandler != null) rtc.RemoteCursorReceived -= _cursorHandler;
        if (_videoFrameHandler != null) rtc.RemoteVideoFrameReceived -= _videoFrameHandler;
        if (_localSpeakingHandler != null) rtc.LocalSpeakingLevelChanged -= _localSpeakingHandler;
        if (_remoteSpeakingHandler != null) rtc.RemoteSpeakingLevelChanged -= _remoteSpeakingHandler;
        if (_connectionHandler != null) rtc.ConnectionStateChanged -= _connectionHandler;
        if (_errorHandler != null) rtc.ErrorOccurred -= _errorHandler;
    }

    /// <summary>设置里开了“录制别人共享给你的屏幕”时，加入方接通后自动开始录制对方屏幕（主持方收不到远端画面，不录）。</summary>
    private async Task AutoStartRecordingIfEnabledAsync()
    {
        if (_autoRecordStarted) return;
        _autoRecordStarted = true;
        try
        {
            if (_sessions.Current?.IsHost == false && AppSettings.Current.RecordRemoteScreen && !IsRecording)
                await ToggleRecordingAsync();
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("系统", $"自动录制启动失败：{ex.Message}", DateTime.Now));
        }
    }

    /// <summary>发本地光标位置给对方（相对坐标 0..1）；未接通时不发。</summary>
    public Task SendCursorAsync(double x, double y)
    {
        var rtc = _rtc;
        if (rtc == null || !rtc.IsConnected) return Task.CompletedTask;
        return rtc.SendCursorAsync(x, y);
    }

    private void UpdateSpeaking(bool isLocal, double level)
    {
        var speaking = level > SpeakingThreshold;
        if (isLocal)
        {
            if (speaking) _localSpeakingUntil = DateTime.UtcNow.AddMilliseconds(400);
            var shouldShow = DateTime.UtcNow <= _localSpeakingUntil;
            if (shouldShow == _localSpeaking) return;
            _localSpeaking = shouldShow;
            RunOnUi(() => SetSpeaking("me", shouldShow));
        }
        else
        {
            if (speaking) _remoteSpeakingUntil = DateTime.UtcNow.AddMilliseconds(400);
            var shouldShow = DateTime.UtcNow <= _remoteSpeakingUntil;
            if (shouldShow == _remoteSpeaking) return;
            _remoteSpeaking = shouldShow;
            RunOnUi(() =>
            {
                var remote = Participants.FirstOrDefault(p => p.Id != "me");
                if (remote != null) remote.IsSpeaking = shouldShow;
            });
        }
    }

    private void SetSpeaking(string participantId, bool speaking)
    {
        var p = Participants.FirstOrDefault(x => x.Id == participantId);
        if (p != null) p.IsSpeaking = speaking;
    }

    public Task LeaveAsync() => _sessions.LeaveCurrentAsync();

    public async Task ToggleMicAsync()
    {
        if (_rtc == null) return;
        await _rtc.SetMicMutedAsync(!IsMicMuted);
        IsMicMuted = _rtc.IsMicMuted;
    }

    /// <summary>录制对方屏幕：开/关，文件落到 视频\TokiCast\房间码-时间.h264（VLC 可播）。</summary>
    public async Task ToggleRecordingAsync()
    {
        if (_rtc == null) return;
        if (IsRecording)
        {
            await _rtc.StopRecordingAsync();
            IsRecording = false;
            Messages.Add(new ChatMessage("系统", "录制已停止。", DateTime.Now));
        }
        else
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TokiCast");
            var file = Path.Combine(folder, $"{_sessions.Current?.RoomId ?? "session"}-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
            await _rtc.StartRecordingAsync(file);
            IsRecording = true;
            Messages.Add(new ChatMessage("系统", $"开始录制对方屏幕：{file}", DateTime.Now));
        }
    }

    public void SendChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Messages.Add(new ChatMessage(AppSettings.Current.UserName, text.Trim(), DateTime.Now));
        // 经 WebRTC DataChannel 发给对端；channel 未打开时只本地显示。
        _ = _rtc?.SendChatAsync(text.Trim());
    }

    internal void OnRemoteCursor(string peerId, string name, double x, double y)
        => RemoteCursorMoved?.Invoke(peerId, name, x, y);
}
