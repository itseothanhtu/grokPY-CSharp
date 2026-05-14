using GrokPY.Core.Models;
using GrokPY.Services;
using GrokPY.Services.Api;
using GrokPY.Services.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace GrokPY.App.Views.Tabs;

public partial class TabImageToVideo :  System.Windows.Controls.UserControl
{
    private readonly VeoImageToVideoService  _veoService;
    private readonly GrokImageToVideoService _grokService;
    private readonly WorkflowControl         _control;
    private readonly SettingsManager         _settings;
    private string? _lastOutputDir;
    private List<string> _selectedImages = new();

    public TabImageToVideo()
    {
        InitializeComponent();
        _veoService  = App.Services.GetRequiredService<VeoImageToVideoService>();
        _grokService = App.Services.GetRequiredService<GrokImageToVideoService>();
        _control     = App.Services.GetRequiredService<WorkflowControl>();
        _settings    = App.Services.GetRequiredService<SettingsManager>();
    }

    // ─── Chọn ảnh ────────────────────────────────────────────

    private void BtnBrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var isBatch = ChkBatch.IsChecked == true;

        var dialog = new OpenFileDialog
        {
            Title       = isBatch ? "Chọn nhiều ảnh" : "Chọn ảnh đầu vào",
            Filter      = "Ảnh|*.jpg;*.jpeg;*.png;*.webp",
            Multiselect = isBatch
        };

        if (dialog.ShowDialog() != true) return;

        _selectedImages = dialog.FileNames.ToList();
        TxtImagePath.Text = isBatch
            ? $"{_selectedImages.Count} ảnh đã chọn"
            : _selectedImages[0];

        // Preview ảnh đầu tiên
        if (!isBatch)
        {
            try
            {
                var bmp = new BitmapImage(new Uri(_selectedImages[0]));
                ImgPreview.Source        = bmp;
                ImgPreviewBorder.Visibility = Visibility.Visible;
            }
            catch { ImgPreviewBorder.Visibility = Visibility.Collapsed; }
        }
        else
        {
            ImgPreviewBorder.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Generate ─────────────────────────────────────────────

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImages.Count == 0)
        {
            ShowStatus("⚠️ Vui lòng chọn ảnh đầu vào", error: true);
            return;
        }

        SetBusy(true);
        LstOutput.Items.Clear();

        var prompt = TxtPrompt.Text.Trim();
        var aspectRatio = CmbAspect.SelectedIndex == 1
            ? VideoAspectRatio.Landscape
            : VideoAspectRatio.Portrait;
        var duration = int.TryParse(
            (CmbDuration.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            out var d) ? d : 6;

        var cfg       = _settings.LoadSettings();
        var outputDir = string.IsNullOrEmpty(cfg.OutputDirectory)
            ? null
            : Path.Combine(cfg.OutputDirectory, "Videos",
                DateTime.Now.ToString("yyyy-MM-dd"));
        _lastOutputDir = outputDir;

        foreach (var imagePath in _selectedImages)
        {
            ShowStatus($"🎬 Đang xử lý: {Path.GetFileName(imagePath)}");

            VideoGenResult result;
            try
            {
                if (RbGrok.IsChecked == true)
                {
                    result = await _grokService.GenerateAsync(
                        imagePath, prompt, aspectRatio,
                        outputDir: outputDir,
                        onProgress: msg =>
                            Dispatcher.Invoke(() => ShowStatus(msg)));
                }
                else
                {
                    result = await _veoService.GenerateAsync(
                        imagePath, prompt, aspectRatio, duration,
                        outputDir: outputDir,
                        onProgress: msg =>
                            Dispatcher.Invoke(() => ShowStatus(msg)));
                }

                Dispatcher.Invoke(() =>
                {
                    if (result.Ok)
                    {
                        foreach (var f in result.SavedFiles)
                        {
                            LstOutput.Items.Add(Path.GetFileName(f));
                            _lastOutputDir = Path.GetDirectoryName(f);
                        }
                    }
                    else
                    {
                        LstOutput.Items.Add($"❌ {Path.GetFileName(imagePath)}: {result.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    LstOutput.Items.Add($"❌ {Path.GetFileName(imagePath)}: {ex.Message}"));
            }

            // Delay nhỏ giữa các ảnh
            if (_selectedImages.Count > 1)
                await Task.Delay(2000);
        }

        Dispatcher.Invoke(() =>
        {
            ShowStatus($"✅ Xong! {LstOutput.Items.Count} video");
            SetBusy(false);
        });
    }

    // ─── Stop ─────────────────────────────────────────────────

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _control.Stop();
        ShowStatus("⏹️ Đã dừng");
        SetBusy(false);
    }

    // ─── Open Folder ──────────────────────────────────────────

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastOutputDir
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (Directory.Exists(dir))
            Process.Start("explorer.exe", dir);
    }

    // ─── Helpers ──────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        BtnGenerate.IsEnabled     = !busy;
        BtnStop.IsEnabled         = busy;
        BtnBrowseImage.IsEnabled  = !busy;
        PbProgress.Visibility     = busy ? Visibility.Visible : Visibility.Collapsed;
    }

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
