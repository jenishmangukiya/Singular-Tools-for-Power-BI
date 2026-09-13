using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace SingularTools_App.Shell;

/// <summary>A single overlay notification card with a slide/fade animation.</summary>
public sealed partial class ToastCard : UserControl
{
    private readonly Storyboard _inStoryboard = new();
    private readonly Storyboard _outStoryboard = new();

    public ToastCard()
    {
        InitializeComponent();
        BuildAnimations();
    }

    public void Apply(ToastSeverity severity, string message)
    {
        MessageText.Text = message;

        InfoIcon.Visibility = severity == ToastSeverity.Informational ? Visibility.Visible : Visibility.Collapsed;
        SuccessIcon.Visibility = severity == ToastSeverity.Success ? Visibility.Visible : Visibility.Collapsed;
        WarningIcon.Visibility = severity == ToastSeverity.Warning ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = severity == ToastSeverity.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    public void PlayIn()
    {
        Opacity = 0;
        ToastTransform.X = 24;
        _inStoryboard.Begin();
    }

    public void PlayOut(Action onCompleted)
    {
        _outStoryboard.Completed += (_, _) => onCompleted();
        _outStoryboard.Begin();
    }

    private void BuildAnimations()
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fadeIn, this);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");

        var slideIn = new DoubleAnimation
        {
            From = 24,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slideIn, ToastTransform);
        Storyboard.SetTargetProperty(slideIn, "X");

        _inStoryboard.Children.Add(fadeIn);
        _inStoryboard.Children.Add(slideIn);

        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fadeOut, this);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        _outStoryboard.Children.Add(fadeOut);
    }
}
