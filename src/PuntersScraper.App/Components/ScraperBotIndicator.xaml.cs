using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PuntersScraper.App.Components;

public partial class ScraperBotIndicator : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ScraperBotIndicator),
        new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Width of the track the mascot walks back and forth across — bound to the
    /// ActualWidth of whatever container hosts it in MainWindow, since the header's available
    /// width depends on the window's own size and can't be known from XAML alone.</summary>
    public static readonly DependencyProperty TrackWidthProperty = DependencyProperty.Register(
        nameof(TrackWidth), typeof(double), typeof(ScraperBotIndicator),
        new PropertyMetadata(0.0, OnTrackWidthChanged));

    public double TrackWidth
    {
        get => (double)GetValue(TrackWidthProperty);
        set => SetValue(TrackWidthProperty, value);
    }

    private Storyboard? _walkStoryboard;

    public ScraperBotIndicator()
    {
        InitializeComponent();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ScraperBotIndicator)d).UpdateActiveAnimationState((bool)e.NewValue);
    }

    private static void OnTrackWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ScraperBotIndicator)d).RebuildWalkStoryboard();
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateActiveAnimationState(IsActive);
        RebuildWalkStoryboard();
    }

    private void UpdateActiveAnimationState(bool active)
    {
        var storyboard = (Storyboard)Resources["ActiveStoryboard"];
        if (active) storyboard.Begin(this, true);
        else storyboard.Stop(this);
    }

    /// <summary>Builds (and restarts) the always-on "walking" animation — bob, arm swing, head
    /// tilt, and side-to-side run across <see cref="TrackWidth"/>. Built in code rather than XAML
    /// because the run distance depends on measured layout, which isn't known until the control
    /// (and its host) have actually been sized.</summary>
    private void RebuildWalkStoryboard()
    {
        if (!IsLoaded) return;

        _walkStoryboard?.Stop(this);

        var maxOffset = Math.Max(0, TrackWidth - ActualWidth);
        // Roughly constant walking speed regardless of how wide the header currently is, with a
        // sensible floor so a very narrow window doesn't make it flicker back and forth instantly.
        var runSeconds = Math.Max(2.5, maxOffset / 55.0);

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        AddAnimation(sb, "RunTransform", TranslateTransform.XProperty, 0, maxOffset, runSeconds, autoReverse: true);
        AddAnimation(sb, "BobTransform", TranslateTransform.YProperty, 0, -4, 0.5, autoReverse: true);
        AddAnimation(sb, "HeadTiltTransform", RotateTransform.AngleProperty, -6, 6, 1.6, autoReverse: true);

        var armLeft = new DoubleAnimation(-40, -10, TimeSpan.FromSeconds(0.6)) { AutoReverse = true };
        Storyboard.SetTargetName(armLeft, "ArmLeft");
        Storyboard.SetTargetProperty(armLeft, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        sb.Children.Add(armLeft);

        var armRight = new DoubleAnimation(40, 10, TimeSpan.FromSeconds(0.6))
        {
            AutoReverse = true,
            BeginTime = TimeSpan.FromSeconds(0.3)
        };
        Storyboard.SetTargetName(armRight, "ArmRight");
        Storyboard.SetTargetProperty(armRight, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        sb.Children.Add(armRight);

        _walkStoryboard = sb;
        _walkStoryboard.Begin(this, true);
    }

    private static void AddAnimation(
        Storyboard sb, string targetName, DependencyProperty property, double from, double to, double seconds, bool autoReverse)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = autoReverse };
        Storyboard.SetTargetName(anim, targetName);
        Storyboard.SetTargetProperty(anim, new PropertyPath(property));
        sb.Children.Add(anim);
    }
}
