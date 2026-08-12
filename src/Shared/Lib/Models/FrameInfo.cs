namespace DrawnUi.Camera
{
    public enum FrameSourceType
    {
        Default = 0,
        
        Preview = 1,
        
        Other = 2,
    }

    public struct DrawableFrame
    {
        public SKCanvas Canvas { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public FrameSourceType SourceType { get; set; }

        public bool IsPreview
        {
            get
            {
                return SourceType == FrameSourceType.Preview;
            }
        }
        public TimeSpan Time { get; set; }

        /// <summary>
        /// Scale factor relative to recording frame size.
        /// Recording frames: Scale = 1.0
        /// Preview frames: Scale = previewSize / recordingSize (e.g., 0.3 for smaller preview)
        /// Use this to multiply your drawing scale for consistent overlay sizing.
        /// </summary>
        public float Scale { get; set; }
    }
}
