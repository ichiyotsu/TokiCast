using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using P2PKiwi.WinUI3.Services;
using P2PKiwi.WinUI3.ViewModels;
using Windows.System;
using Windows.UI;

namespace P2PKiwi.WinUI3.Views;

public sealed partial class SessionPage : Page
{
    private readonly SessionViewModel _vm = new(new SessionManager());
    private readonly Dictionary<string, (Polygon Cursor, TextBlock Label)> _cursors = new();

    public SessionPage()
    {
        InitializeComponent();
        ParticipantList.ItemsSource = _vm.Participants;
        ChatList.ItemsSource = _vm.Messages;
        _vm.RemoteCursorMoved += OnRemoteCursorMoved;
        // “允许”按钮在代码里创建一次再挂上去，不用 x:Bind 在加载时构造，
        // 避免未处理异常导致整页加载闪退。
        var allowButton = new Button { Content = "允许" };
        allowButton.Click += (_, _) => ControlRequestBar.IsOpen = false;
        ControlRequestBar.ActionButton = allowButton;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string link) _ = _vm.JoinAsync(link);
    }

    private void OnRemoteCursorMoved(string peerId, string name, double x, double y)
    {
        // 远端光标：按相对坐标（0..1）换算到画面尺寸，画一个带名字的箭头。
        // 原版 native/overlay-sidecar 做的是系统级光标叠加；WinUI 版先做应用内叠加。
        DispatcherQueue.TryEnqueue(() =>
        {
            var w = RemoteVideo.ActualWidth; var h = RemoteVideo.ActualHeight;
            if (!_cursors.TryGetValue(peerId, out var entry))
            {
                var cursor = new Polygon
                {
                    Points = new PointCollection { new(0, 0), new(0, 16), new(4, 12), new(7, 18), new(9, 16), new(6, 11), new(11, 11) },
                    Fill = new SolidColorBrush(Color.FromArgb(255, 0x67, 0x50, 0xA4)),
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1,
                };
                var label = new TextBlock { Text = name, FontSize = 11, Padding = new Thickness(4, 1, 4, 1) };
                CursorLayer.Children.Add(cursor);
                CursorLayer.Children.Add(label);
                _cursors[peerId] = (cursor, label);
                entry = (cursor, label);
            }
            Canvas.SetLeft(entry.Cursor, x * w);
            Canvas.SetTop(entry.Cursor, y * h);
            Canvas.SetLeft(entry.Label, x * w + 12);
            Canvas.SetTop(entry.Label, y * h + 12);
        });
    }

    private void MicButton_Click(object sender, RoutedEventArgs e) => _vm.ToggleMic();
    private void ControlButton_Click(object sender, RoutedEventArgs e) => _vm.ToggleRemoteControl();
    private void ChatButton_Click(object sender, RoutedEventArgs e) => ChatInput.Focus(FocusState.Programmatic);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { /* AppWindow.Presenter.RequestPresentation */ }
    private void Leave_Click(object sender, RoutedEventArgs e) { _ = _vm.LeaveAsync(); Frame.Navigate(typeof(HomePage)); }
    private void SendChat_Click(object sender, RoutedEventArgs e) { _vm.SendChat(ChatInput.Text); ChatInput.Text = ""; }
    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { SendChat_Click(sender, e); e.Handled = true; }
    }
}
