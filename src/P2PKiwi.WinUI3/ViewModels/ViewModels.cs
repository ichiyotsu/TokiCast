using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using P2PKiwi.WinUI3.Models;
using P2PKiwi.WinUI3.Services;

namespace P2PKiwi.WinUI3.ViewModels;

public sealed class HomeViewModel : ViewModelBase
{
    private readonly ISessionManager _sessions;
    public ObservableCollection<RecentSession> RecentSessions { get; } = new();

    public HomeViewModel(ISessionManager sessions) => _sessions = sessions;

    public async Task LoadSourcesAsync(ComboBox picker)
    {
        // TODO(Windows)：GraphicsCapturePicker.PickSingleItemAsync 让用户选显示器/窗口。
        // 这里先放占位，等真机上再接 picker（picker 必须在 UI 线程调）。
        await Task.CompletedTask;
        picker.ItemsSource = new[] { "主显示器 (1920×1080)", "副显示器", "某个窗口…" };
        picker.SelectedIndex = 0;
    }

    public async Task<string?> StartSharingAsync(string? source, bool shareAudio, bool allowControl)
    {
        var session = await _sessions.CreateSessionAsync(new SessionOptions
        {
            SourceName = source ?? "主显示器",
            ShareSystemAudio = shareAudio,
            AllowRemoteControl = allowControl,
        });
        RecentSessions.Insert(0, new RecentSession(session.InviteLink, DateTime.Now.ToString("MM-dd HH:mm")));
        return session.InviteLink;
    }
}

public sealed class SessionViewModel : ViewModelBase
{
    private readonly ISessionManager _sessions;
    public ObservableCollection<Participant> Participants { get; } = new();
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public event Action<string, string, double, double>? RemoteCursorMoved;

    public SessionViewModel(ISessionManager sessions) => _sessions = sessions;

    public async Task JoinAsync(string link)
    {
        var session = await _sessions.JoinSessionAsync(link);
        Participants.Clear();
        foreach (var p in session.Participants) Participants.Add(p);
        Messages.Add(new ChatMessage("系统", $"已加入房间 {session.RoomId}，连接为点对点直连。"));
    }

    public Task LeaveAsync() => _sessions.LeaveCurrentAsync();

    public void ToggleMic() { /* TODO：MediaCapture / WebRTC 音频轨静音 */ }
    public void ToggleRemoteControl() { /* TODO：向对方发控制请求，等对方点允许 */ }
    public void SendChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Messages.Add(new ChatMessage("我", text.Trim()));
        // TODO：经 WebRTC DataChannel 发给各 peer（MLS 加密的聊天通道）。
    }

    internal void OnRemoteCursor(string peerId, string name, double x, double y)
        => RemoteCursorMoved?.Invoke(peerId, name, x, y);
}
