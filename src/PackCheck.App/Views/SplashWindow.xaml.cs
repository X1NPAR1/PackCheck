using System.Windows;
using System.Windows.Media.Animation;
using PackCheck.App.Infrastructure;

namespace PackCheck.App.Views;

/// <summary>Startup window. Progress is driven by the real startup steps in App.OnStartup.</summary>
public partial class SplashWindow : Window
{
    private double _shown;

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            Animate(Root, OpacityProperty, 1, 320);
            Animate(RootScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 1, 380);
            Animate(RootScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 1, 380);
            Animate(Logo, OpacityProperty, 1, 600, 120);
            Animate(LogoShift, System.Windows.Media.TranslateTransform.YProperty, 0, 600, 120);
            if (Motion.Enabled)
            {
                var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
                Ring.BeginAnimation(Controls.ProgressRing.SpinProperty, spin);
            }
        };
    }

    /// <summary>Moves the ring smoothly to <paramref name="progress"/> (0..1) and slides in the new step text.</summary>
    public void Report(double progress, string step)
    {
        var from = _shown;
        _shown = progress;
        var anim = new DoubleAnimation(from, progress, Motion.D(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.CurrentTimeInvalidated += (_, _) => Percent.Text = $"{Math.Round(Ring.Value * 100):0}%";
        anim.Completed += (_, _) => Percent.Text = $"{Math.Round(progress * 100):0}%";
        Ring.BeginAnimation(Controls.ProgressRing.ValueProperty, anim);
        if (progress >= 1)
        {
            Ring.BeginAnimation(Controls.ProgressRing.SpinProperty, null);
        }

        if (StepText.Text == step)
        {
            return;
        }

        if (!Motion.Enabled)
        {
            StepText.Text = step;
            return;
        }

        var outAnim = new DoubleAnimation(0, -18, Motion.D(140));
        outAnim.Completed += (_, _) =>
        {
            StepText.Text = step;
            StepShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(18, 0, Motion.D(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            StepText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.D(220)));
        };
        StepShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, outAnim);
        StepText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Motion.D(140)));
    }

    public Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource();
        var a = new DoubleAnimation(0, Motion.D(220));
        a.Completed += (_, _) => tcs.TrySetResult();
        Root.BeginAnimation(OpacityProperty, a);
        if (!Motion.Enabled)
        {
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    private static void Animate(System.Windows.Media.Animation.IAnimatable target, DependencyProperty dp, double to, int ms, int delay = 0)
    {
        var a = new DoubleAnimation(to, Motion.D(ms)) { BeginTime = TimeSpan.FromMilliseconds(Motion.Enabled ? delay : 0), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        target.BeginAnimation(dp, a);
    }
}
