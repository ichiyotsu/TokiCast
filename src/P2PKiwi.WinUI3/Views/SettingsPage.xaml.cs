using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using P2PKiwi.WinUI3.Services;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.UI;

namespace P2PKiwi.WinUI3.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        _ = LoadDevicesAsync();
    }

    private void LoadSettings()
    {
        var s = AppSettings.Current;
        UserNameBox.Text = s.UserName;
        ForegroundBox.Text = s.ForegroundColor;
        BackgroundBox.Text = s.BackgroundColor;
        LanguageBox.SelectedItem = s.Language;
        CameraBox.SelectedItem = s.CameraDevice;
        MicrophoneBox.SelectedItem = s.MicrophoneDevice;
        MicOnConnectBox.IsChecked = s.MicOnConnect;
        RecordBox.IsChecked = s.RecordRemoteScreen;
        ResolutionBox.SelectedItem = s.Resolution;
        FrameRateSlider.Value = s.FrameRate;
        BitrateSlider.Value = s.BitrateMbps;
        BonjourBox.IsChecked = s.BonjourEnabled;
        BonjourUrlBox.Text = s.BonjourUrl;
        DebugLogBox.IsChecked = s.DebugLog;
        E2eeBox.IsChecked = s.E2eeEnabled;
        SFrameBox.IsChecked = s.SFrameEnabled;
        StunTurnBox.Text = s.StunTurnJson;
        UpdateColorPreview(ForegroundBox.Text, ForegroundPreview);
        UpdateColorPreview(BackgroundBox.Text, BackgroundPreview);
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var cameras = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector());
            foreach (var d in cameras)
                if (!CameraBox.Items.Contains(d.Name)) CameraBox.Items.Add(d.Name);
            var mics = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            foreach (var d in mics)
                if (!MicrophoneBox.Items.Contains(d.Name)) MicrophoneBox.Items.Add(d.Name);
            // 恢复上次保存的选中设备（列表里没有就留在“系统默认”）。
            if (CameraBox.Items.Contains(AppSettings.Current.CameraDevice))
                CameraBox.SelectedItem = AppSettings.Current.CameraDevice;
            if (MicrophoneBox.Items.Contains(AppSettings.Current.MicrophoneDevice))
                MicrophoneBox.SelectedItem = AppSettings.Current.MicrophoneDevice;
        }
        catch
        {
            // 枚举设备失败时只留“系统默认”，不打断设置页。
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.UserName = UserNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(s.UserName)) s.UserName = "Kiwi";
        s.ForegroundColor = ForegroundBox.Text.Trim();
        s.BackgroundColor = BackgroundBox.Text.Trim();
        s.Language = LanguageBox.SelectedItem as string ?? "zh";
        s.CameraDevice = CameraBox.SelectedItem as string ?? "系统默认";
        s.MicrophoneDevice = MicrophoneBox.SelectedItem as string ?? "系统默认";
        s.MicOnConnect = MicOnConnectBox.IsChecked == true;
        s.RecordRemoteScreen = RecordBox.IsChecked == true;
        s.Resolution = ResolutionBox.SelectedItem as string ?? "1920 × 1080";
        s.FrameRate = (int)FrameRateSlider.Value;
        s.BitrateMbps = (int)BitrateSlider.Value;
        s.BonjourEnabled = BonjourBox.IsChecked == true;
        s.BonjourUrl = BonjourUrlBox.Text.Trim();
        s.DebugLog = DebugLogBox.IsChecked == true;
        s.E2eeEnabled = E2eeBox.IsChecked == true;
        s.SFrameEnabled = SFrameBox.IsChecked == true;
        s.StunTurnJson = StunTurnBox.Text.Trim();
        AppSettings.Save();
        BasicSavedHint.Visibility = Visibility.Visible;
        MediaSavedHint.Visibility = Visibility.Visible;
        AdvancedSavedHint.Visibility = Visibility.Visible;
    }

    private void ForegroundBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateColorPreview(ForegroundBox.Text, ForegroundPreview);

    private void BackgroundBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateColorPreview(BackgroundBox.Text, BackgroundPreview);

    private static void UpdateColorPreview(string hex, Border preview)
    {
        try
        {
            var h = hex.Trim().TrimStart('#');
            if (h.Length != 6) return;
            var color = Color.FromArgb(255,
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
            preview.Background = new SolidColorBrush(color);
        }
        catch
        {
            // 非法颜色值不改预览。
        }
    }
}
