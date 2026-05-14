using GrokPY.Services;
using GrokPY.Services.Auth;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;

// KHÔNG using System.Windows.Forms — dùng WPF dialog thuần

namespace GrokPY.App.Views.Tabs;

public partial class TabSettings : System.Windows.Controls.UserControl
{
    private readonly SettingsManager _settings;
    private readonly LicenseManager  _license;
    private readonly LoginService    _loginService;

    public TabSettings()
    {
        InitializeComponent();
        _settings     = App.Services.GetRequiredService<SettingsManager>();
        _license      = App.Services.GetRequiredService<LicenseManager>();
        _loginService = App.Services.GetRequiredService<LoginService>();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var cfg = _settings.LoadSettings();
        var acc = cfg.Account1;

        TxtEmail.Text       = acc.Email;
        TxtAccessToken.Text = acc.AccessToken;
        TxtSessionId.Text   = acc.SessionId;
        TxtProjectId.Text   = acc.ProjectId;
        TxtCookie.Text      = acc.Cookie;
        TxtOutputDir.Text   = cfg.OutputDirectory;

        foreach (ComboBoxItem item in CmbAccountType.Items)
            if (item.Content?.ToString() == acc.TypeAccount)
            { CmbAccountType.SelectedItem = item; break; }

        var machineId = GrokPY.Core.Helpers.MachineIdHelper.MakeMachineId();
        TxtMachineId.Text =
            $"Machine ID: {machineId[..Math.Min(16, machineId.Length)]}...";

        _ = RefreshLicenseStatusAsync();
    }

    private void BtnSaveAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = _settings.LoadSettings();
            cfg.Account1.Email = TxtEmail.Text.Trim();
            cfg.Account1.TypeAccount =
                (CmbAccountType.SelectedItem as ComboBoxItem)?
                .Content?.ToString() ?? "ULTRA";

            if (TxtPassword.Password.Length > 0)
                cfg.Account1.EncryptedPassword =
                    _settings.EncryptPassword(TxtPassword.Password);

            _settings.SaveSettings(cfg);
            ShowStatus("✅ Đã lưu tài khoản", success: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"❌ Lỗi: {ex.Message}", success: false);
        }
    }

    private void BtnSaveTokens_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.SaveTokens(
                TxtSessionId.Text.Trim(),
                TxtProjectId.Text.Trim(),
                TxtAccessToken.Text.Trim(),
                TxtCookie.Text.Trim());
            ShowStatus("✅ Đã lưu tokens", success: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"❌ Lỗi: {ex.Message}", success: false);
        }
    }

    private async void BtnAutoLogin_Click(object sender, RoutedEventArgs e)
    {
        var email    = TxtEmail.Text.Trim();
        var password = TxtPassword.Password;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ShowStatus("⚠️ Nhập email và mật khẩu", success: false);
            return;
        }

        BtnAutoLogin.IsEnabled = false;
        ShowStatus("🔐 Đang đăng nhập...", success: true);

        var result = await _loginService.LoginAsync(
            email, password,
            onProgress: msg =>
                Dispatcher.Invoke(() => ShowStatus(msg, success: true)));

        BtnAutoLogin.IsEnabled = true;

        if (result.Ok)
        {
            ShowStatus("✅ Đăng nhập thành công!", success: true);
            LoadSettings();
        }
        else
        {
            ShowStatus($"❌ {result.Message}", success: false);
        }
    }

    // ─── Browse Output Dir (dùng WPF thuần, không cần WinForms) ──
    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        // WPF không có FolderBrowserDialog built-in
        // Dùng OpenFileDialog với workaround
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Chọn thư mục output (chọn bất kỳ file nào trong thư mục)",
            CheckFileExists  = false,
            FileName         = "Chọn thư mục này",
            Filter           = "Thư mục|*.none"
        };

        if (dlg.ShowDialog() == true)
        {
            var dir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
            TxtOutputDir.Text = dir;
            var cfg = _settings.LoadSettings();
            cfg.OutputDirectory = dir;
            _settings.SaveSettings(cfg);
            ShowStatus("✅ Đã lưu thư mục output", success: true);
        }
    }

    private async void BtnActivateLicense_Click(object sender, RoutedEventArgs e)
    {
        var key = TxtLicenseKey.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            ShowStatus("⚠️ Nhập license key", success: false);
            return;
        }

        BtnActivateLicense.IsEnabled = false;
        TxtLicenseStatus.Text        = "⏳ Đang xác thực...";

        var result = await _license.ActivateAsync(key);
        BtnActivateLicense.IsEnabled = true;

        var green = System.Windows.Media.Color.FromRgb(0, 184, 148);
        var red   = System.Windows.Media.Color.FromRgb(255, 107, 107);

        if (result.IsValid)
        {
            var exp = result.ExpiresAt?.ToString("dd/MM/yyyy") ?? "Không giới hạn";
            TxtLicenseStatus.Text = $"✅ License hợp lệ — hết hạn: {exp}";
            TxtLicenseStatus.Foreground =
                new System.Windows.Media.SolidColorBrush(green);
        }
        else
        {
            TxtLicenseStatus.Text = $"❌ {result.Message}";
            TxtLicenseStatus.Foreground =
                new System.Windows.Media.SolidColorBrush(red);
        }
    }

    private async Task RefreshLicenseStatusAsync()
    {
        var result = await _license.CheckLicenseAsync();
        var yellow = System.Windows.Media.Color.FromRgb(255, 183, 3);
        var green  = System.Windows.Media.Color.FromRgb(0, 184, 148);

        Dispatcher.Invoke(() =>
        {
            if (result.IsValid)
            {
                var exp = result.ExpiresAt?.ToString("dd/MM/yyyy") ?? "Không giới hạn";
                TxtLicenseStatus.Text = $"✅ License hợp lệ — hết hạn: {exp}";
                TxtLicenseStatus.Foreground =
                    new System.Windows.Media.SolidColorBrush(green);
            }
            else
            {
                TxtLicenseStatus.Text = "⚠️ Chưa có license hợp lệ";
                TxtLicenseStatus.Foreground =
                    new System.Windows.Media.SolidColorBrush(yellow);
            }
        });
    }

    private void ShowStatus(string msg, bool success)
    {
        var green = System.Windows.Media.Color.FromRgb(0, 184, 148);
        var red   = System.Windows.Media.Color.FromRgb(255, 107, 107);
        TxtStatus.Text       = msg;
        TxtStatus.Foreground = success
            ? new System.Windows.Media.SolidColorBrush(green)
            : new System.Windows.Media.SolidColorBrush(red);
    }
}
