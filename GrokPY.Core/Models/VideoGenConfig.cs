namespace GrokPY.Core.Models;

/// <summary>
/// Cấu hình tạo video — tương đương VideoGenConfig trong Python gốc
/// </summary>
public class VideoGenConfig
{
    /// <summary>9:16 | 16:9 | 1:1</summary>
    public string AspectRatio { get; set; } = "9:16";

    /// <summary>Độ dài video tính bằng giây</summary>
    public int VideoLengthSeconds { get; set; } = 6;

    /// <summary>480p | 720p</summary>
    public string ResolutionName { get; set; } = "480p";

    /// <summary>
    /// Chuyển sang dictionary để inject vào JS payload
    /// </summary>
    public Dictionary<string, object> ToDict() => new()
    {
        ["aspectRatio"] = AspectRatio,
        ["videoLength"] = VideoLengthSeconds,
        ["resolutionName"] = ResolutionName.ToLower() switch
        {
            "720p" => "720p",
            _ => "480p"
        }
    };
}

/// <summary>
/// Tỷ lệ khung hình video
/// </summary>
public static class VideoAspectRatio
{
    public const string Landscape = "VIDEO_ASPECT_RATIO_LANDSCAPE";
    public const string Portrait = "VIDEO_ASPECT_RATIO_PORTRAIT";
}

/// <summary>
/// Tỷ lệ khung hình ảnh
/// </summary>
public static class ImageAspectRatio
{
    public const string Landscape = "IMAGE_ASPECT_RATIO_LANDSCAPE";
    public const string Portrait = "IMAGE_ASPECT_RATIO_PORTRAIT";
    public const string Square = "IMAGE_ASPECT_RATIO_SQUARE";
}

/// <summary>
/// Model key tạo video Veo 3.1 — tương đương các hằng số trong Python gốc
/// </summary>
public static class VeoModelKey
{
    // ULTRA account
    public const string UltraLandscape = "veo_3_1_t2v_fast_ultra";
    public const string UltraPortrait = "veo_3_1_t2v_fast_portrait_ultra";
    public const string UltraLandscapeRelaxed = "veo_3_1_t2v_fast_ultra_relaxed";
    public const string UltraPortraitRelaxed = "veo_3_1_t2v_fast_portrait_ultra_relaxed";

    // NORMAL / PRO account
    public const string NormalLandscape = "veo_3_1_t2v_fast";
    public const string NormalPortrait = "veo_3_1_t2v_fast_portrait";
}

/// <summary>
/// Model key tạo ảnh — tương đương CREATE_IMAGE_MODEL_TO_KEY trong Python gốc
/// </summary>
public static class ImageModelKey
{
    public const string Imagen4 = "IMAGEN_3_5";
    public const string NanoBanana = "GEM_PIX";
    public const string NanoBanana2 = "NARWHAL";
    public const string NanoBananaPro = "GEM_PIX_2";
}
