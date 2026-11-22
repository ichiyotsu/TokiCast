using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using P2PKiwi.WinUI3.Services;
using P2PKiwi.WinUI3.ViewModels;

namespace P2PKiwi.WinUI3.Views;

public sealed partial class HomePage : Page
{
    private readonly HomeViewModel _vm = new(new SessionManager());

    public HomePage()
    {
        InitializeComponent();
        RecentList.ItemsSource = _vm.RecentSessions;
        _ = _vm.LoadSourcesAsync(SourcePicker);
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
            InviteLinkBox.Text = link;
            // E2EE 徽标等 MLS 密钥真正建立后再显示，现在不打开。
            Frame.Navigate(typeof(SessionPage));
        }
        catch (Exception ex)
        {
            // 开始失败改为弹窗提示，不让未处理异常直接闪退。
            await ShowErrorAsync("开始分享失败", ex.Message);
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(JoinLinkBox.Text)) return;
        try
        {
            Frame.Navigate(typeof(SessionPage), JoinLinkBox.Text.Trim());
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
