using GrokPY.Services;
using GrokPY.Services.Auth;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GrokPY.App.Views.Tabs;

public partial class TabGrokSettings : System.Windows.Controls.UserControl
{
    private readonly StatsigDiscovery     _statsig;
    private readonly ChromeProfileManager _profileManager;
    private readonly SettingsManager      _settings;

    public TabGrokSettings()
    {
        InitializeComponent();
        _statsig        = App.Services.GetRequiredService<StatsigDiscovery>();
        _profileManager = App.Services.GetRequiredService<ChromeProfileManager>();
        _settings       = App.Services.GetRequiredService<SettingsManager>();
        LoadData();
    }

    // ─── Load ──────────────────────────────────────────────────

    private void LoadData()
    {
        // Load Grok profiles
        RefreshProfileList();

        // Load statsig nếu có cache
        _ = LoadStatsigFromCacheAsync();
    }

    private async Task LoadStatsigFromCacheAsync()
    {
        var result = await _statsig.GetStatsigIdAsync();
        if (result.Ok)
        {
            TxtStatsigId.Text     = result.StatsigId;
            TxtStatsigStatus.Text = $"Cache từ: {result.FetchedAt:dd/MM/yyyy HH:mm}";
            TxtGrokCookie.Text    = result.Cookie;
        }
    }

    private void RefreshProfileList()
    {
        CmbGrokProfile.Items.Clear();
        var profiles = _profileManager.GetAllProfiles(
            ChromeProfileManager.TypeGrok);

        foreach (var p in profiles)
            CmbGrokProfile.Items.Add(new ComboBoxItem
            {
                Content = p.DisplayName,
                Tag     = p.Name
            });

        if (CmbGrokProfile.Items.Count > 0)
            CmbGrokProfile.SelectedIndex = 0;
        else
        {
            // Tạo profile mặc định nếu chưa có
            _profileManager.CreateProfile("PROFILE_1",
                ChromeProfileManager.TypeGrok);
            CmbGrokProfile.Items.Add(new ComboBoxItem
            {
                Content = "PROFILE_1",
                Tag     = "PROFILE_1"
            });
            CmbGrokProfile.SelectedIndex = 0;
        }
    }

    // ─── Fetch Statsig ────────────────────────────────────────

    private async void BtnFetchStatsig_Click(object sender, RoutedEventArgs e)
    {
        var fetchBtn = sender as System.Windows.Controls.Button; if (fetchBtn != null) fetchBtn.IsEnabled = false;
        ShowStatus("🔄 Đang lấy x-statsig-id...");
        TxtStatsigId.Text = "Đang lấy...";

        var profileName = (CmbGrokProfile.SelectedItem as ComboBoxItem)?
            .Tag?.ToString() ?? "PROFILE_1";
        var profileDir = _profileManager.GetProfileDir(
            profileName, ChromeProfileManager.TypeGrok);

        var result = await _statsig.GetStatsigIdAsync(
            profileDir,
            onProgress: msg => Dispatcher.Invoke(() => ShowStatus(msg)),
            forceRefresh: true);

        if (fetchBtn != null) fetchBtn.IsEnabled = true;

        if (result.Ok)
        {
            TxtStatsigId.Text     = result.StatsigId;
            TxtGrokCookie.Text    = result.Cookie;
            TxtStatsigStatus.Text = $"Lấy lúc: {result.FetchedAt:dd/MM/yyyy HH:mm}";
            ShowStatus("✅ Lấy x-statsig-id thành công!");
        }
        else
        {
            TxtStatsigId.Text = "(thất bại)";
            ShowStatus($"❌ {result.Error}", error: true);
        }
    }

    // ─── Profile Management ───────────────────────────────────

    private void BtnNewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = _profileManager.GetNextProfileName(ChromeProfileManager.TypeGrok);
        _profileManager.CreateProfile(name, ChromeProfileManager.TypeGrok);
        RefreshProfileList();
        ShowStatus($"✅ Tạo profile mới: {name}");
    }

    private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var item = CmbGrokProfile.SelectedItem as ComboBoxItem;
        if (item == null) return;

        var name = item.Tag?.ToString() ?? string.Empty;
        var result = MessageBox.Show(
            $"Xoá profile '{name}'?\nThao tác này không thể hoàn tác.",
            "Xác nhận",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _profileManager.DeleteProfile(name, ChromeProfileManager.TypeGrok);
        RefreshProfileList();
        ShowStatus($"🗑️ Đã xoá profile: {name}");
    }

    // ─── Save Cookie ──────────────────────────────────────────

    private void BtnSaveCookie_Click(object sender, RoutedEventArgs e)
    {
        // Grok cookie lưu riêng (không dùng chung với Google)
        var cookie = TxtGrokCookie.Text.Trim();
        if (string.IsNullOrEmpty(cookie))
        {
            ShowStatus("⚠️ Cookie trống", error: true);
            return;
        }

        // Lưu vào file riêng cho Grok
        var grokCookiePath = Path.Combine(_settings.DataDir, "grok_cookie.txt");
        File.WriteAllText(grokCookiePath, cookie);
        ShowStatus("✅ Đã lưu Grok cookie");
    }

    // ─── Test Connection ──────────────────────────────────────

    private async void BtnTestGrok_Click(object sender, RoutedEventArgs e)
    {
        BtnTestGrok.IsEnabled = false;
        TxtTestResult.Text    = "🔄 Đang kiểm tra...";

        try
        {
            // Thử gọi Grok API đơn giản
            using var http = new System.Net.Http.HttpClient();
            http.Timeout   = TimeSpan.FromSeconds(10);

            var grokCookiePath = Path.Combine(_settings.DataDir, "grok_cookie.txt");
            var cookie = File.Exists(grokCookiePath)
                ? File.ReadAllText(grokCookiePath)
                : string.Empty;

            if (!string.IsNullOrEmpty(cookie))
                http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);

            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "x-statsig-id", TxtStatsigId.Text);

            var resp = await http.GetAsync("https://grok.com/rest/user/me");

            TxtTestResult.Text = resp.IsSuccessStatusCode
                ? $"✅ Kết nối thành công! (HTTP {(int)resp.StatusCode})"
                : $"⚠️ HTTP {(int)resp.StatusCode} — Có thể cần đăng nhập lại";

            TxtTestResult.Foreground = resp.IsSuccessStatusCode
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 184, 148))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 183, 3));
        }
        catch (Exception ex)
        {
            TxtTestResult.Text = $"❌ Lỗi: {ex.Message}";
            TxtTestResult.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 107, 107));
        }
        finally
        {
            BtnTestGrok.IsEnabled = true;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────

    private void ShowStatus(string msg, bool error = false)
    {
        TxtStatus.Text       = msg;
        TxtStatus.Foreground = error
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 107, 107))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0, 184, 148));
    }
}
