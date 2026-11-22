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
        if (!_cursors.TryGetValue(peerId, out var entry))
        {
            var cursor = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(255, 90, 180, 255)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                Points = new PointCollection { new(0, 0), new(0, 16), new(4, 12), new(7, 18), new(9, 16), new(6, 11), new(11, 11) },
            };
            var label = new TextBlock
            {
                Text = name,
                Background = new SolidColorBrush(Color.FromArgb(255, 90, 180, 255)),
                Foreground = new SolidColorBrush(Colors.White),
                Padding = new Thickness(4, 1, 4, 1),
            };
            CursorLayer.Children.Add(cursor);
            CursorLayer.Children.Add(label);
            entry = (cursor, label);
            _cursors[peerId] = entry;
        }
        Canvas.SetLeft(entry.Cursor, x * RemoteVideo.ActualWidth);
        Canvas.SetTop(entry.Cursor, y * RemoteVideo.ActualHeight);
        Canvas.SetLeft(entry.Label, x * RemoteVideo.ActualWidth + 10);
        Canvas.SetTop(entry.Label, y * RemoteVideo.ActualHeight + 16);
    }

    private void RemoteVideo_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(RemoteVideo).Position;
        _vm.SendLocalCursor(p.X / RemoteVideo.ActualWidth, p.Y / RemoteVideo.ActualHeight);
    }

    private void StartSharing_Click(object sender, RoutedEventArgs e) { _ = _vm.StartSharingAsync(); }
    private void StopSharing_Click(object sender, RoutedEventArgs e) { _vm.StopSharing(); }
    private void RequestControl_Click(object sender, RoutedEventArgs e) { _ = _vm.RequestControlAsync(); }
    private void AllowControlToggle_Toggled(object sender, RoutedEventArgs e) { _vm.AllowControl = AllowControlToggle.IsOn; }
    private void CopyInviteLink_Click(object sender, RoutedEventArgs e) { _ = _vm.CopyInviteLinkAsync(); }
    private void LeaveSession_Click(object sender, RoutedEventArgs e) { Frame.Navigate(typeof(HomePage)); }
    private void DumpLog_Click(object sender, RoutedEventArgs e) { /* TODO: dump log */ }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { /* AppWindow.Presenter.RequestPresentation */ }
    private void MicButton_Click(object sender, RoutedEventArgs e) { MicButton.IsChecked = !MicButton.IsChecked; }
    private void SendMessage_Click(object sender, RoutedEventArgs e) { /* TODO: send chat message */ }
    private void RemoteVideo_KeyDown(object sender, KeyRoutedEventArgs e) { _vm.SendKey(e.Key, e.KeyStatus.IsKeyReleased); }
    private void Home_Click(object sender, RoutedEventArgs e) { Frame.Navigate(typeof(HomePage)); }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { Title = "设置", Content = "设置界面尚未实现。", CloseButtonText = "知道了", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
