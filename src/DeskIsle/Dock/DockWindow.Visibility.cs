using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskIsle.Dock;

public partial class DockBar
{
    internal event Action? RevealReady;
    private readonly TranslateTransform visibilityOffset = new();
    private bool wantedVisible, fadingOut;
    private long visibilityVersion;
    private EventHandler? revealFrame;
    internal void CancelRevealFrame()
    {
        if (revealFrame == null) return;
        CompositionTarget.Rendering -= revealFrame; revealFrame = null;
    }
    internal bool IsHiding => fadingOut;

    internal void RevealAnimated()
    {
        if (closed) return;
        if (wantedVisible && IsVisible) return;
        var wasVisible = IsVisible;
        CancelRevealFrame();
        wantedVisible = true; fadingOut = false;
        var version = ++visibilityVersion;
        DockAssembly.RenderTransform = visibilityOffset;
        if (!wasVisible)
        {
            Opacity = 0; BeginAnimation(OpacityProperty, null);
            visibilityOffset.Y = 8; visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null);
            // The layered window is transparent until layout and native placement are ready.
            Show(); UpdateLayout(); Place();
            // Keep the first composed frame transparent, then animate from the prepared layout.
            var frames = 0;
            revealFrame = (_, _) =>
            {
                if (closed || version != visibilityVersion || !wantedVisible) { CancelRevealFrame(); return; }
                if (++frames < 2) return;
                CancelRevealFrame();
                DeskIsle.Services.StartupDiagnostics.Mark("dock-reveal-frame-ready");
                UpdateLayout(); Place();
                AnimateVisibility(1, 0, 180, version, false);
                RevealReady?.Invoke();
            };
            CompositionTarget.Rendering += revealFrame;
            return;
        }
        AnimateVisibility(1, 0, 180, version, false);
        RevealReady?.Invoke();
    }

    internal void HideAnimated(bool immediate = false)
    {
        if (closed) return;
        CancelRevealFrame();
        if (immediate)
        {
            ++visibilityVersion; wantedVisible = false; fadingOut = false;
            Opacity = 0; visibilityOffset.Y = 6;
            BeginAnimation(OpacityProperty, null); visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null);
            Hide(); return;
        }
        if (!IsVisible || fadingOut) return;
        wantedVisible = false; fadingOut = true;
        AnimateVisibility(0, 6, 140, ++visibilityVersion, true);
    }

    private void AnimateVisibility(double opacity, double offset, int milliseconds, long version, bool hide)
    {
        var currentOpacity = Opacity;
        var currentOffset = visibilityOffset.Y;
        // Set the base value while the old clock still holds its current frame.
        // Removing a clock must never briefly expose the old (often zero) base value.
        Opacity = currentOpacity; BeginAnimation(OpacityProperty, null);
        visibilityOffset.Y = currentOffset; visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null);
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
            Opacity = opacity; visibilityOffset.Y = offset;
            BeginAnimation(OpacityProperty, null);
            visibilityOffset.BeginAnimation(TranslateTransform.YProperty, null);
            fadingOut = false;
            if (hide && !wantedVisible) Hide();
        };
        visibilityOffset.BeginAnimation(TranslateTransform.YProperty, slide);
        BeginAnimation(OpacityProperty, fade);
    }
}
