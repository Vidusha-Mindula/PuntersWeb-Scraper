using System.Windows;
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

    public ScraperBotIndicator()
    {
        InitializeComponent();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ScraperBotIndicator)d).UpdateAnimationState((bool)e.NewValue);
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e) => UpdateAnimationState(IsActive);

    private void UpdateAnimationState(bool active)
    {
        var storyboard = (Storyboard)Resources["ActiveStoryboard"];
        if (active) storyboard.Begin(this, true);
        else storyboard.Stop(this);
    }
}
