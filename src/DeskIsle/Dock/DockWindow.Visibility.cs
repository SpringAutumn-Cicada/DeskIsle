using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskIsle.Dock;

public partial class DockBar
{
    private readonly TranslateTransform visibilityOffset = new();
    private bool wantedVisible, fadingOut;
    private long visibilityVersion;
    internal bool IsHiding => fadingOut;

    internal void RevealAnimated()
    {
        if (closed) return;
        if (wantedVisible && IsVisible) return;
        var wasVisible = IsVisible;
        wantedVisible = true; fadingOut = false;
        var version = ++visibilityVersion;
        DockAssembly.RenderTransform = visibilityOffset;
        if (!wasVisible)
        {
            BeginAnimation(OpacityProperty, null); Opacity = 0;
            visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null); visibilityOffset.Y = 8;
            // The layered window is transparent until layout and native placement are ready.
            Show(); UpdateLayout(); Place();
        }
        AnimateVisibility(1, 0, 180, version, false);
    }

    internal void HideAnimated(bool immediate = false)
    {
        if (closed) return;
        if (immediate)
        {
            ++visibilityVersion; wantedVisible = false; fadingOut = false;
            BeginAnimation(OpacityProperty, null); visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 0; visibilityOffset.Y = 6; Hide(); return;
        }
        if (!IsVisible || fadingOut) return;
        wantedVisible = false; fadingOut = true;
        AnimateVisibility(0, 6, 140, ++visibilityVersion, true);
    }

    private void AnimateVisibility(double opacity, double offset, int milliseconds, long version, bool hide)
    {
        var currentOpacity = Opacity;
        var currentOffset = visibilityOffset.Y;
        BeginAnimation(OpacityProperty, null); Opacity = currentOpacity;
        visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null); visibilityOffset.Y = currentOffset;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = opacity; visibilityOffset.Y = offset; fadingOut = false;
            if (hide) Hide();
            return;
        }
        var easing = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut };
        var fade = new DoubleAnimation(currentOpacity, opacity, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing };
        var slide = new DoubleAnimation(currentOffset, offset, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (closed || version != visibilityVersion) return;
            BeginAnimation(OpacityProperty, null); Opacity = opacity;
            visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null); visibilityOffset.Y = offset;
            fadingOut = false;
            if (hide && !wantedVisible) Hide();
        };
        visibilityOffset.BeginAnimation(TranslateTransform.YProperty, slide);
        BeginAnimation(OpacityProperty, fade);
    }
}
