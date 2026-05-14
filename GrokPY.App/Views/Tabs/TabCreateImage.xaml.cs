using GrokPY.Core.Models;
using GrokPY.Services;
using GrokPY.Services.Api;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GrokPY.App.Views.Tabs;

public partial class TabCreateImage :  System.Windows.Controls.UserControl
{
    private readonly GoogleImageService _imageService;
    private readonly SettingsManager    _settings;
    private string? _lastOutputDir;

    private static readonly Dictionary<string, string> ModelMap = new()
    {
        ["Imagen 4"]        = ImageModelKey.Imagen4,
        ["Nano Banana"]     = ImageModelKey.NanoBanana,
        ["Nano Banana 2"]   = ImageModelKey.NanoBanana2,
        ["Nano Banana Pro"] = ImageModelKey.NanoBananaPro
    };

    public TabCreateImage()
    {
        InitializeComponent();
        _imageService = App.Services.GetRequiredService<GoogleImageService>();
        _settings     = App.Services.GetRequiredService<SettingsManager>();
    }

    private async void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        var prompt = TxtPrompt.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        { ShowStatus("⚠️ Nhập prompt", error: true); return; }

        SetBusy(true);
        LstOutput.Items.Clear();

        var modelName = (CmbModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                     ?? "Imagen 4";
        var modelKey  = ModelMap.GetValueOrDefault(modelName, ImageModelKey.Imagen4);

        var aspectRatio = CmbAspect.SelectedIndex switch
        {
            1 => ImageAspectRatio.Landscape,
            2 => ImageAspectRatio.Square,
            _ => ImageAspectRatio.Portrait
        };

        var count = int.TryParse(
            (CmbCount.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var c) ? c : 1;
        var seed = int.TryParse(TxtSeed.Text, out var s) ? s : -1;

        var cfg       = _settings.LoadSettings();
        var outputDir = string.IsNullOrEmpty(cfg.OutputDirectory)
            ? null : Path.Combine(cfg.OutputDirectory, "Images",
                DateTime.Now.ToString("yyyy-MM-dd"));

        // Batch?
        var prompts = ChkBatch.IsChecked == true
            ? TxtPrompt.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList()
            : new List<string> { prompt };

        foreach (var p in prompts)
        {
            var result = await _imageService.GenerateAsync(
                p, modelKey, aspectRatio, count, seed,
                outputDir: outputDir,
                onProgress: msg => Dispatcher.Invoke(() => ShowStatus(msg)));

            Dispatcher.Invoke(() =>
            {
                if (result.Ok)
                    foreach (var f in result.SavedFiles)
                    {
                        LstOutput.Items.Add(Path.GetFileName(f));
                        _lastOutputDir = Path.GetDirectoryName(f);
                    }
                else
                    LstOutput.Items.Add($"❌ {result.Message}");
            });
        }

        Dispatcher.Invoke(() =>
        {
            ShowStatus($"✅ Xong! {LstOutput.Items.Count} ảnh");
            SetBusy(false);
        });
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => SetBusy(false);

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastOutputDir
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
    }

    private void SetBusy(bool busy)
    {
        BtnCreate.IsEnabled = !busy;
        BtnStop.IsEnabled   = busy;
        PbProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
