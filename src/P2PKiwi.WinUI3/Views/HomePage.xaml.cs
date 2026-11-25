using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using P2PKiwi.WinUI3.Services;
using P2PKiwi.WinUI3.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace P2PKiwi.WinUI3.Views;

public sealed partial class HomePage : Page
{
    private readonly HomeViewModel _vm = new(SessionManager.Shared);
    private string? _lastLink;
    private bool _isStarting;

    public HomePage()
    {
        InitializeComponent();
        RecentList.ItemsSource = _vm.RecentSessions;
        _ = _vm.LoadRecentAsync();
    }

    private async void StartSharing_Click(object sender, RoutedEventArgs e)
    {
        // 防重入：等 ICE 收集（1~5 秒）期间再点会多建一个 RTC 连接，先建的那个没人关。
        if (_isStarting) return;
        _isStarting = true;
        try
        {
            if (App.MainWindow == null)
            {
                await ShowErrorAsync("开始分享失败", "主窗口未就绪。");
                return;
            }
            // 先让用户在系统选择器里选要分享的显示器/窗口；取消则不开始。
            var item = await ScreenCaptureVideoSource.PickCaptureItemAsync(WindowNative.GetWindowHandle(App.MainWindow));
            if (item is null) return;
            var link = await _vm.StartSharingAsync(
                ShareAudioCheck.IsChecked == true,
                AllowControlCheck.IsChecked == true,
                item);
            if (link is null) return;
            // 第一次开始分享默认同时复制链接。
            _lastLink = link;
            CopyToClipboard(link);
            CopyLinkButton.IsEnabled = true;
            // 不立刻跳会话页：等对方发回 answer 链接，粘贴到下面的 answer 框并点「允许加入」再进会话页。
        }
        catch (Exception ex)
        {
            // 开始失败改为弹窗提示，不让未处理异常直接闪退。
            await ShowErrorAsync("开始分享失败", ex.Message);
        }
        finally
        {
            _isStarting = false;
        }
    }

    /// <summary>主持方：粘贴对方发回的 answer 链接，完成连接后进会话页。</summary>
    private async void AllowJoin_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AnswerLinkBox.Text)) return;
        try
        {
            await _vm.CompleteSharingAsync(AnswerLinkBox.Text.Trim());
            App.MainWindow?.NavigateToSession();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("允许加入失败", ex.Message);
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_lastLink is not null) CopyToClipboard(_lastLink);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            // SetContent 要求应用在前台，刚经过系统选择器 + 1~5 秒 ICE 等待时前台很可能已丢，
            // 会抛 InvalidOperationException；SetContentWithOptions 不会因此抛。
            Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = true, IsRoamable = false });
        }
        catch
        {
            // 复制失败不打断分享流程，链接可用「复制链接」按钮重试。
        }
    }

    /// <summary>点一条最近会话，一键重新加入（用存下的邀请链接）。</summary>
    private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is P2PKiwi.WinUI3.Models.RecentSession recent
            && !string.IsNullOrWhiteSpace(recent.InviteLink))
        {
            App.MainWindow?.NavigateToSession(recent.InviteLink);
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(JoinLinkBox.Text)) return;
        try
        {
            App.MainWindow?.NavigateToSession(JoinLinkBox.Text.Trim());
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("加入会话失败", ex.Message);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
