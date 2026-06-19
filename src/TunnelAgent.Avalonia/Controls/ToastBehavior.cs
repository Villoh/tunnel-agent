using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TunnelAgent.Controls;

/// <summary>
/// Attached behavior that fades a control in/out when its <see cref="IsOpenProperty"/>
/// changes, collapsing it from layout once the fade-out completes. Bind
/// <c>ctrl:Toast.IsOpen</c> instead of <c>IsVisible</c> on toast/alert borders.
/// </summary>
public static class Toast
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);

    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOpen", typeof(Toast));

    // Monotonic token used to ignore stale fade-out completions when the state flips quickly.
    private static readonly AttachedProperty<int> GenerationProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("Generation", typeof(Toast));

    static Toast()
    {
        IsOpenProperty.Changed.AddClassHandler<Control>(OnIsOpenChanged);
    }

    public static void SetIsOpen(Control control, bool value) => control.SetValue(IsOpenProperty, value);

    public static bool GetIsOpen(Control control) => control.GetValue(IsOpenProperty);

    private static void OnIsOpenChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        EnsureTransition(control);

        var generation = control.GetValue(GenerationProperty) + 1;
        control.SetValue(GenerationProperty, generation);

        if (e.GetNewValue<bool>())
        {
            // Snap to transparent without animating, make visible, then fade in.
            var transitions = control.Transitions;
            control.Transitions = null;
            control.Opacity = 0;
            control.Transitions = transitions;
            control.IsVisible = true;

            Dispatcher.UIThread.Post(() =>
            {
                if (control.GetValue(GenerationProperty) == generation)
                    control.Opacity = 1;
            }, DispatcherPriority.Render);
        }
        else
        {
            control.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                if (control.GetValue(GenerationProperty) == generation)
                    control.IsVisible = false;
            }, FadeDuration);
        }
    }

    private static void EnsureTransition(Control control)
    {
        if (control.Transitions is { } existing &&
            existing.Any(t => t is DoubleTransition { Property: { } p } && p == Visual.OpacityProperty))
            return;

        control.Transitions ??= new Transitions();
        control.Transitions.Add(new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = FadeDuration,
            Easing = new CubicEaseInOut()
        });
    }
}
