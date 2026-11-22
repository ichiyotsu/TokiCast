using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using P2PKiwi.WinUI3.Services;
using P2PKiwi.WinUI3.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace P2PKiwi.WinUI3.Views;

public sealed partial class HomePage : Page
{
    private readonly HomeViewModel _vm = new(SessionManager.Shared);
    private string? _lastLink;

    public HomePage()
    {
        InitializeComponent();
        RecentList.ItemsSource = _vm.RecentSessions;
        _ = _vm.LoadSourcesAsync(SourcePicker);
        _ = _vm.LoadRecentAsync();
    }

    private async void StartSharing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var link = await _vm.StartSharingAsync(
                SourcePicker.SelectedItem as string,
                ShareAudioCheck.IsChecked == true,
                AllowControlCheck.IsChecked == true);
            if (link is null) return;
            // 第一次开始分享默认同时复制链接。
            _lastLink = link;
            CopyToClipboard(link);
            CopyLinkButton.IsEnabled = true;
            // E2EE 徽标等 MLS 密钥真正建立后再显示，现在不打开。
            App.MainWindow?.NavigateToSession();
        }
        catch (Exception ex)
        {
            // 开始失败改为弹窗提示，不让未处理异常直接闪退。
            await ShowErrorAsync("开始分享失败", ex.Message);
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_lastLink is not null) CopyToClipboard(_lastLink);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
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
