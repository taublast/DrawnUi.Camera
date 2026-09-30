namespace CameraTests.UI
{
    public static class ShaderEffectHelper
    {
        public static string GetTitle(ShaderEffect value)
        {
            return value switch
            {
                ShaderEffect.None => "Off",
                ShaderEffect.Zoom => "Zoom",
                ShaderEffect.Movie => "Movie",
                ShaderEffect.Wes => "Sand",
                ShaderEffect.Runner => "Sci-Fi",
                ShaderEffect.Desat => "Noir",
                ShaderEffect.BW => "B&W",
                ShaderEffect.Sketch => "Sketch",
                _ => value.ToString()
            };
        }

        public static string GetFilename(ShaderEffect value)
        {
            return value switch
            {
                ShaderEffect.Zoom => @"Shaders/photozoom.sksl",
                ShaderEffect.Movie => @"Shaders/film.sksl",
                ShaderEffect.Wes => @"Shaders/wes.sksl",
                ShaderEffect.Desat => @"Shaders/snyder.sksl",
                ShaderEffect.Runner => @"Shaders/blade.sksl",
                ShaderEffect.BW => @"Shaders/bwclassic.sksl",
                // a local test asset that is not part of the repository: without the file the effect draws the plain frame
                ShaderEffect.Sketch => @"Shaders/sketch.sksl",
                _ => string.Empty
            };
        }
    }
}
