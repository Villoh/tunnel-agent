using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.Controls;

public class SlidingTabBar : Panel
{
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SlidingTabBar, int>(nameof(SelectedIndex), defaultValue: 0);

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    // Plain CLR list — XAML Content property, populated by Avalonia before OnInitialized
    [Avalonia.Metadata.Content]
    public List<SlidingTab> Tabs { get; } = new();

    private readonly Border _outer;
    private readonly Panel _inner;
    private readonly Border _pill;
    private readonly TranslateTransform _pillTranslate;
    private readonly Grid _buttonGrid;
    private readonly List<IDisposable> _resourceSubscriptions = new();
    private bool _initialised;
    private bool _isSizeChangedSubscribed;

    static SlidingTabBar()
    {
        SelectedIndexProperty.Changed.AddClassHandler<SlidingTabBar>((s, _) => s.MovePill(animate: true));
    }

    public SlidingTabBar()
    {
        _pillTranslate = new TranslateTransform(0, 0);

        _pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#146CF9")), // fallback; bound to AccentBrush
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = _pillTranslate,
        };

        _buttonGrid = new Grid();

        _inner = new Panel();
        _inner.Children.Add(_pill);
        _inner.Children.Add(_buttonGrid);

        _outer = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1C1E26")),  // fallback; bound to CardBgBrush
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A4A")), // fallback; bound to CardBorderBrush
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            ClipToBounds = true,
            Child = _inner,
        };

        Children.Add(_outer);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        BuildButtons();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        BindThemeResources();

        _initialised = true;
        if (!_isSizeChangedSubscribed)
        {
            SizeChanged += OnSizeChanged;
            _isSizeChangedSubscribed = true;
        }

        // Post pill positioning after first layout pass so bounds are available
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { UpdateForegrounds(); MovePill(animate: false); },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_isSizeChangedSubscribed)
        {
            SizeChanged -= OnSizeChanged;
            _isSizeChangedSubscribed = false;
        }

        DisposeResourceSubscriptions();
        _initialised = false;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => MovePill(animate: false);

    private void BindThemeResources()
    {
        DisposeResourceSubscriptions();

        _resourceSubscriptions.Add(this.GetResourceObservable("AccentBrush").Subscribe(new ResourceObserver(value =>
        {
            if (value is IBrush brush) _pill.Background = brush;
        })));
        _resourceSubscriptions.Add(this.GetResourceObservable("CardBgBrush").Subscribe(new ResourceObserver(value =>
        {
            if (value is IBrush brush) _outer.Background = brush;
        })));
        _resourceSubscriptions.Add(this.GetResourceObservable("CardBorderBrush").Subscribe(new ResourceObserver(value =>
        {
            if (value is IBrush brush) _outer.BorderBrush = brush;
        })));
        _resourceSubscriptions.Add(this.GetResourceObservable("FgBrush").Subscribe(new ResourceObserver(_ => UpdateForegrounds())));
        UpdateForegrounds();
    }

    private void DisposeResourceSubscriptions()
    {
        foreach (var subscription in _resourceSubscriptions)
            subscription.Dispose();
        _resourceSubscriptions.Clear();
    }

    private void BuildButtons()
    {
        _buttonGrid.Children.Clear();
        _buttonGrid.ColumnDefinitions.Clear();

        for (var i = 0; i < Tabs.Count; i++)
            _buttonGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (var i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            var idx = i;

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Bind(TextBlock.TextProperty, new Binding(nameof(SlidingTab.Header)) { Source = tab });
            Control content = label;
            PackIconSimpleIcons? icon = null;
            Path? customPath = null;
            if (tab.HasIcon)
            {
                if (tab.CustomIconData is not null)
                {
                    customPath = new Path
                    {
                        Data    = Avalonia.Media.PathGeometry.Parse(tab.CustomIconData),
                        Width   = 18,
                        Height  = 18,
                        Stretch = Avalonia.Media.Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                else
                {
                    icon = new PackIconSimpleIcons
                    {
                        Kind = tab.IconKind,
                        Width = 18,
                        Height = 18,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                Control iconControl = (Control?)customPath ?? icon!;
                content = iconControl;
            }

            var btn = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 5),
                FontSize = 13,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                [Grid.ColumnProperty] = idx,
                [!ToolTip.TipProperty] = new Binding(nameof(SlidingTab.Header)) { Source = tab },
            };

            // Subtle hover/press — same feel as sidebar buttons
            btn.Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover").Template().OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x0F, 0, 0, 0))) }
            });
            btn.Styles.Add(new Style(x => x.OfType<Button>().Class(":pressed").Template().OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0))) }
            });

            btn.PointerEntered += (_, _) => UpdateForegrounds();
            btn.PointerExited += (_, _) => UpdateForegrounds();
            btn.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name is "IsPointerOver" or "IsPressed")
                    Avalonia.Threading.Dispatcher.UIThread.Post(UpdateForegrounds, Avalonia.Threading.DispatcherPriority.Render);
            };

            btn.Click += (_, _) =>
            {
                tab.Command?.Execute(tab.CommandParameter);
                SelectedIndex = idx;
            };

            _buttonGrid.Children.Add(btn);
        }
    }

    private void MovePill(bool animate)
    {
        if (Tabs.Count == 0) return;

        var count = Tabs.Count;
        var idx = Math.Clamp(SelectedIndex, 0, count - 1);

        if (_buttonGrid.Children.Count <= idx) return;
        var btn = _buttonGrid.Children[idx] as Button;
        if (btn is null) return;

        var btnBounds = btn.Bounds;
        if (btnBounds.Width <= 0) return;

        var pillWidth = btnBounds.Width;
        var targetX   = btnBounds.X;

        _pill.Width = pillWidth;

        if (animate && _initialised)
        {
            var fromX = _pillTranslate.X;
            if (Math.Abs(fromX - targetX) < 0.5) return;

            var anim = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = new CubicEaseInOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(TranslateTransform.XProperty, fromX) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(TranslateTransform.XProperty, targetX) }
                    }
                }
            };
            _ = anim.RunAsync(_pill);
        }
        else
        {
            _pillTranslate.X = targetX;
        }

        UpdateForegrounds();
    }

    private void UpdateForegrounds()
    {
        if (Tabs.Count == 0) return;

        var idx = Math.Clamp(SelectedIndex, 0, Tabs.Count - 1);
        var app = Avalonia.Application.Current;
        var isDark = ActualThemeVariant == ThemeVariant.Dark ||
                     app?.RequestedThemeVariant == ThemeVariant.Dark ||
                     app?.ActualThemeVariant == ThemeVariant.Dark;

        var inactiveBrush = isDark
            ? Brushes.White
            : this.FindResource("FgBrush") as IBrush
              ?? app?.FindResource("FgBrush") as IBrush
              ?? new SolidColorBrush(Color.Parse("#1A1D23"));

        for (var i = 0; i < _buttonGrid.Children.Count; i++)
        {
            if (_buttonGrid.Children[i] is not Button btn) continue;
            var brush = i == idx ? Brushes.White : inactiveBrush;
            btn.ClearValue(Button.ForegroundProperty);
            btn.Foreground = brush;
            if (btn.Content is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is TextBlock text)
                        text.Foreground = brush;
                    else if (child is PackIconSimpleIcons icon)
                        icon.Foreground = brush;
                    else if (child is Path svgPath)
                        svgPath.Fill = brush;
                }
            }
            else if (btn.Content is TextBlock label)
            {
                label.Foreground = brush;
            }
            else if (btn.Content is PackIconSimpleIcons iconDirect)
            {
                iconDirect.Foreground = brush;
            }
            else if (btn.Content is Path pathDirect)
            {
                pathDirect.Fill = brush;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _outer.Measure(availableSize);
        return _outer.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _outer.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

internal sealed class ResourceObserver(Action<object?> onNext) : IObserver<object?>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(object? value) => onNext(value);
}

public class SlidingTab : AvaloniaObject
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<SlidingTab, string>(nameof(Header), "");

    public static readonly StyledProperty<System.Windows.Input.ICommand?> CommandProperty =
        AvaloniaProperty.Register<SlidingTab, System.Windows.Input.ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<SlidingTab, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<PackIconSimpleIconsKind> IconKindProperty =
        AvaloniaProperty.Register<SlidingTab, PackIconSimpleIconsKind>(nameof(IconKind), PackIconSimpleIconsKind.OpenAi);

    public static readonly StyledProperty<bool> HasIconProperty =
        AvaloniaProperty.Register<SlidingTab, bool>(nameof(HasIcon));

    public static readonly StyledProperty<string?> CustomIconDataProperty =
        AvaloniaProperty.Register<SlidingTab, string?>(nameof(CustomIconData));

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public System.Windows.Input.ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public PackIconSimpleIconsKind IconKind
    {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public bool HasIcon
    {
        get => GetValue(HasIconProperty);
        set => SetValue(HasIconProperty, value);
    }

    public string? CustomIconData
    {
        get => GetValue(CustomIconDataProperty);
        set => SetValue(CustomIconDataProperty, value);
    }
}
