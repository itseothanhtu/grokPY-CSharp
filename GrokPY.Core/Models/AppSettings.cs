namespace GrokPY.Core.Models;

/// <summary>
/// Toàn bộ cài đặt ứng dụng — lưu vào config.json
/// </summary>
public class AppSettings
{
    public AccountConfig Account1 { get; set; } = new();

    /// <summary>Random | Fixed</summary>
    public string SeedMode { get; set; } = "Random";

    public int SeedValue { get; set; } = 9797;

    /// <summary>Model ảnh đang chọn: Imagen 4 | Nano Banana | ...</summary>
    public string CreateImageModel { get; set; } = "Imagen 4";

    /// <summary>Profile Chrome hiện tại</summary>
    public string CurrentProfile { get; set; } = "PROFILE_1";

    /// <summary>Option 1 = dùng token từ file | Option 2 = dùng browser</summary>
    public string TokenOption { get; set; } = "Option 1";

    /// <summary>Thư mục lưu output (ảnh/video)</summary>
    public string OutputDirectory { get; set; } = string.Empty;
}
