namespace DrawnUi.Camera;

/// <summary>
/// Video recording quality presets
/// </summary>
public enum VideoQuality
{
    /// <summary>
    /// Low quality (480p, 30 fps)
    /// </summary>
    Low,
    
    /// <summary>
    /// Standard quality (720p, 30 fps)
    /// </summary>
    Standard,
    
    /// <summary>
    /// High quality (1080p; 30 fps on iOS / Mac Catalyst, 60 fps on Android and Windows, by design)
    /// </summary>
    High,
    
    /// <summary>
    /// Ultra quality (4K where available, up to 60 fps)
    /// </summary>
    Ultra,
    
    /// <summary>
    /// Manual format selection using VideoFormatIndex
    /// </summary>
    Manual
}
