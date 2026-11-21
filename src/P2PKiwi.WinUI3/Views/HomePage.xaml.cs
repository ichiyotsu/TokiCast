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
        var link = await _vm.StartSharingAsync(
            SourcePicker.SelectedItem as string,
            ShareAudioCheck.IsChecked == true,
            AllowControlCheck.IsChecked == true);
        if (link is null) return;
        InviteLinkBox.Text = link;
        E2eeBar.IsOpen = true;
        Frame.Navigate(typeof(SessionPage));
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(JoinLinkBox.Text)) return;
        Frame.Navigate(typeof(SessionPage), JoinLinkBox.Text.Trim());
    }
}
