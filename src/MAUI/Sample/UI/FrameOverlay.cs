using CameraTests.Visualizers;
using DrawnUi.Camera;

namespace CameraTests.UI;

/// <summary>
/// Video frame overlay that renders an audio EQ visualizer (and optionally other data)
/// directly into the camera preview and recording frames.
///
/// Extends <see cref="CameraOverlayLayout"/> so orientation transforms are handled
/// automatically — child content only needs to be laid out for portrait.
/// </summary>
public class FrameOverlay : CameraOverlayLayout, IAppOverlay
{
    public SkiaLabel CaptionsLabel => _captionsLabel;

    public AudioVisualizer Visualizer
    {
        get => visualizer;
        set
        {
            if (Equals(value, visualizer))
            {
                return;
            }

            visualizer = value;
            OnPropertyChanged();
        }
    }

    private SkiaLabel _labelVisualizerName;
    private SkiaLabel _captionsLabel;
    private SkiaShape _captionsPanel;
    private AudioVisualizer visualizer;
    private SkiaShape panelVisualizer;
    private AnimatedShaderEffect _captionExitEffect;
    private string _latestCaptionText = string.Empty;
    private bool _wasPreviewMode;

    public FrameOverlay()
    {
        VerticalOptions = LayoutOptions.Fill;

        Children = new List<SkiaControl>
        {
            //can place dimmer whatever here

            //then..

            new SkiaLayer()
            {
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                // If you have a more complex overlay could uncomment
                //UseCache = SkiaCacheType.ImageDoubleBuffered,
                // So this would be a double-buffered wrapper:
                // would cache transformed content so each frame encoder
                // thread gets a fast snapshot without stalling on layout work.
                Children =
                {
                    new CameraOverlayContent().Assign(out Content)
                }
            }
        };

        CreateChildren(Content);

        // Keep the label in sync with the current visualizer name
        _labelVisualizerName.ObserveProperty(
            () => Visualizer,
            nameof(Visualizer.VisualizerName),
            me => me.Text = Visualizer?.VisualizerName ?? string.Empty);
    }

    private void CreateChildren(SkiaControl parent)
    {
        parent.Children = new List<SkiaControl>
        {
            //EQ
            new SkiaShape()
            {
                Type = ShapeType.Rectangle,
                UseCache = SkiaCacheType.ImageDoubleBuffered,
                Margin = 16,
                Padding = new Thickness(12, 10, 12, 12),
                WidthRequest = 220,
                HeightRequest = 138,
                CornerRadius = 22,
                BackgroundColor = Color.FromArgb("#A60B1220"),
                StrokeWidth = 1,
                StrokeColor = Color.FromArgb("#3311C5BF"),
                VerticalOptions = LayoutOptions.Start,
                HorizontalOptions = LayoutOptions.End,
                Children =
                {
                    new SkiaLabel("AUDIO EQ")
                    {
                        FontSize = 12,
                        CharacterSpacing = 1,
                        TextColor = Color.FromArgb("#7DEAE5"),
                        UseCache = SkiaCacheType.Operations,
                        HorizontalOptions = LayoutOptions.Start,
                        VerticalOptions = LayoutOptions.Start,
                    },
                    new SkiaLabel()
                        {
                            Margin = new Thickness(0, 18, 0, 0),
                            FontSize = 11,
                            TextColor = Color.FromArgb("#A7B5C6"),
                            UseCache = SkiaCacheType.Operations,
                            HorizontalOptions = LayoutOptions.Start,
                            VerticalOptions = LayoutOptions.Start,
                        }
                        .Assign(out _labelVisualizerName),
                    new AudioVisualizer()
                        {
                            Margin = new Thickness(0, 42, 0, 0),
                            HorizontalOptions = LayoutOptions.Fill,
                            VerticalOptions = LayoutOptions.Fill,
                        }
                        .Assign(out visualizer)
                }
            }.Assign(out panelVisualizer),

            //CAPTIONS 
            new SkiaShape()
            {
                UseCache = SkiaCacheType.Image, //need image to be used as shader source for disappearing anim
                Type = ShapeType.Rectangle,
                CornerRadius = 26,
                Margin = new Thickness(20, 0, 20, 40),
                Padding = new Thickness(20, 16, 20, 18),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.End,
                BackgroundColor = Color.FromArgb("#B30A101A"),
                StrokeColor = Color.FromArgb("#3342D9F6"),
                StrokeWidth = 1,
                Children =
                {
                    new SkiaRichLabel()
                        {
                            FontFamily = "FontText",
                            FontSize = 20,
                            LineHeight = 1.1,
                            TextColor = Colors.White,
                            UseCache = SkiaCacheType.Operations,
                            Margin = new Thickness(0, 0, 0, 0),
                        }
                        .Assign(out _captionsLabel)
                }
            }.Assign(out _captionsPanel),
        };
    }

    public void AddAudioSample(AudioSample sample)
    {
        if (Visualizer != null && panelVisualizer.IsVisible && Visualizer.IsVisible)
        {
            Visualizer.AddSample(sample);
        }
    }

    public string SwitchVisualizer(int index = -1)
    {
        return Visualizer?.SwitchVisualizer(index);
    }

    public void SetAudioMonitoring(bool isAudioMonitoringEnabled)
    {
        panelVisualizer.IsVisible = isAudioMonitoringEnabled;
    }

    /// <summary>
    /// Adapts layout according to whether we're in preview mode or not. In preview, we want to center the captions
    /// Will update only once when mode changes, returns true if changes were made that require a layout update.
    /// </summary>
    /// <param name="isPreview"></param>
    /// <returns></returns>
    public bool AdaptLayoutToMode(bool isPreview)
    {
        if (_wasPreviewMode == isPreview)
        {
            return false;
        }
        _wasPreviewMode = isPreview;

        _captionsPanel.VerticalOptions = isPreview ? LayoutOptions.Center : LayoutOptions.End;

        return true;
    }


    public void SetCaptions(IList<string> spans)
    {
        _latestCaptionText = spans.Count > 0
            ? string.Join(Environment.NewLine, spans)
            : string.Empty;

        if (string.IsNullOrEmpty(_latestCaptionText))
        {
            SetCaptionsVisible(false);
        }
        else
        {
            CancelCaptionExitAnimation();
            CaptionsLabel.Text = _latestCaptionText;
            SetCaptionsVisible(true);
        }
    }

    private void SetCaptionsVisibleInternal(bool isVisible)
    {
        var shouldShow = isVisible && _canShowCaptions && !string.IsNullOrEmpty(_latestCaptionText);

        if (!shouldShow)
        {
            if (!string.IsNullOrEmpty(CaptionsLabel.Text) && _captionsPanel.IsVisible)
            {
                AnimateOut(_captionsPanel);
                return;
            }

            CancelCaptionExitAnimation();
            CaptionsLabel.Text = string.Empty;
            _captionsPanel.IsVisible = false;
            return;
        }

        CancelCaptionExitAnimation();
        if (!string.Equals(CaptionsLabel.Text, _latestCaptionText, StringComparison.Ordinal))
        {
            CaptionsLabel.Text = _latestCaptionText;
        }

        _captionsPanel.IsVisible = true;
    }

    private bool _canShowCaptions;

    public void SetCaptionsVisible(bool isVisible)
    {
        _canShowCaptions = isVisible;
        SetCaptionsVisibleInternal(isVisible);
    }

    private void CancelCaptionExitAnimation()
    {
        if (_captionExitEffect == null)
        {
            return;
        }

        var effect = _captionExitEffect;
        _captionExitEffect = null;
        effect.Stop();
        _captionsPanel.VisualEffects.Remove(effect);
        _captionsPanel.DisposeObject(effect);
    }


    void AnimateOut(SkiaControl control)
    {
        CancelCaptionExitAnimation();

        var animExit = new AnimatedShaderEffect()
        {
            UseBackground = PostRendererEffectUseBackgroud.Once,
            ShaderSource = MauiProgram.ShaderRemoveCaption,
            DurationMs = 400
        };

        _captionExitEffect = animExit;

        animExit.Completed += (sender, args) =>
        {
            if (!ReferenceEquals(_captionExitEffect, animExit))
            {
                return;
            }

            _captionExitEffect = null;
            control.VisualEffects.Remove(animExit);
            if (_canShowCaptions && !string.IsNullOrEmpty(_latestCaptionText))
            {
                CaptionsLabel.Text = _latestCaptionText;
                control.IsVisible = true;
                control.DisposeObject(animExit);
                return;
            }

            CaptionsLabel.Text = string.Empty;
            control.IsVisible = false;
            control.DisposeObject(animExit);
        };

        if (!control.VisualEffects.Contains(animExit))
        {
            control.VisualEffects.Add(animExit);
        }

        animExit.Play();
    }
}
