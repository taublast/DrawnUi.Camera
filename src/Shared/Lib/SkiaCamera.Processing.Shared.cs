namespace DrawnUi.Camera;

// Realtime video-processing contract shared by every flavor (MAUI heads + headless .NET), so the
// SAME subclass code (e.g. the sample's AppCamera shader overrides) compiles and runs on both.
// Each flavor supplies its own compositing surface (GPU via Superview on MAUI, CPU SKSurface on .NET);
// only the override points and the on/off flag live here.
partial class SkiaCamera
{
    /// <summary>
    /// When true, individual camera frames are composited through the processing path
    /// (RenderPreviewForProcessing / RenderFrameForRecording + ProcessPreview / ProcessFrame)
    /// instead of being shown/encoded raw. Default is false.
    /// </summary>
    public bool UseRealtimeVideoProcessing
    {
        get { return (bool)GetValue(UseRealtimeVideoProcessingProperty); }
        set { SetValue(UseRealtimeVideoProcessingProperty, value); }
    }

    public static readonly BindableProperty UseRealtimeVideoProcessingProperty = BindableProperty.Create(
        nameof(UseRealtimeVideoProcessing),
        typeof(bool),
        typeof(SkiaCamera),
        false);

    /// <summary>
    /// Custom frame processor for video capture (recording frames).
    /// Called for each frame being encoded to video. Scale is always 1.0.
    /// </summary>
    public Action<DrawableFrame> ProcessFrame { get; set; }

    /// <summary>
    /// Custom frame processor for preview display.
    /// Called for each preview frame before display. Use PreviewScale to match recording overlay sizing.
    /// </summary>
    public Action<DrawableFrame> ProcessPreview { get; set; }

    /// <summary>
    /// Draws the camera frame into the video encoder frame canvas that will be encoded to video during
    /// recording with processing. Override to customize (e.g. apply a shader). Base draws the frame 1:1.
    /// </summary>
    protected internal virtual void RenderFrameForRecording(SKCanvas canvas, SKImage frame, SKRect src, SKRect dst)
    {
        canvas.DrawImage(frame, src, dst);
    }

    /// <summary>
    /// Draws preview camera frame as background into the canvas that will be used by ProcessPreview
    /// and preview diagnostics.
    /// Override this method to customize the raw preview rendering before any overlays or scaling is applied.
    /// </summary>
    protected virtual void RenderPreviewForProcessing(SKCanvas canvas, SKImage frame)
    {
        canvas.DrawImage(frame, 0, 0);
    }
}
