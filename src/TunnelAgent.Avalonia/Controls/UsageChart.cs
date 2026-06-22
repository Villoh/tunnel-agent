using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TunnelAgent.Controls;

/// <summary>
/// Lightweight area/line chart that renders a single numeric series as a smoothed
/// curve with a gradient fill, matching the dashboard usage graph. Pure custom
/// drawing — no external charting dependency.
/// </summary>
public sealed class UsageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<string>?>(nameof(Labels));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> AreaBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(AreaBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<int> GridLinesProperty =
        AvaloniaProperty.Register<UsageChart, int>(nameof(GridLines), 4);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? AreaBrush
    {
        get => GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public int GridLines
    {
        get => GetValue(GridLinesProperty);
        set => SetValue(GridLinesProperty, value);
    }

    private int _hoverIndex = -1;

    static UsageChart()
    {
        AffectsRender<UsageChart>(ValuesProperty, LabelsProperty, LineBrushProperty, AreaBrushProperty, GridBrushProperty, GridLinesProperty);
        AffectsMeasure<UsageChart>(ValuesProperty);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var values = Values;
        if (values is null || values.Count == 0 || Bounds.Width <= 1)
        {
            _hoverIndex = -1;
        }
        else
        {
            var x = Math.Clamp(e.GetPosition(this).X, 0, Bounds.Width);
            _hoverIndex = values.Count == 1 ? 0 : (int)Math.Round(x / Bounds.Width * (values.Count - 1));
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverIndex = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        var w = bounds.Width;
        var h = bounds.Height;
        if (w <= 1 || h <= 1) return;

        // Horizontal grid lines.
        var gridBrush = GridBrush;
        var lines = Math.Max(0, GridLines);
        if (gridBrush is not null && lines > 0)
        {
            var pen = new Pen(gridBrush, 1);
            for (var i = 0; i <= lines; i++)
            {
                var y = h * i / lines;
                context.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }

        var values = Values;
        if (values is null || values.Count == 0) return;

        var max = values.Max();
        var min = Math.Min(0, values.Min());
        var range = max - min;
        if (range <= double.Epsilon) range = 1;

        const double pad = 6;
        var innerH = h - pad * 2;
        var n = values.Count;

        Point MapPoint(int i)
        {
            var x = n == 1 ? w / 2 : w * i / (n - 1);
            var norm = (values[i] - min) / range;
            var y = pad + innerH * (1 - norm);
            return new Point(x, y);
        }

        var points = new Point[n];
        for (var i = 0; i < n; i++) points[i] = MapPoint(i);

        var lineGeometry = BuildSmoothGeometry(points, false);
        var areaGeometry = BuildSmoothGeometry(points, true, h);

        if (AreaBrush is { } area && areaGeometry is not null)
            context.DrawGeometry(area, null, areaGeometry);

        if (LineBrush is { } line && lineGeometry is not null)
            context.DrawGeometry(null, new Pen(line, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), lineGeometry);

        if (_hoverIndex >= 0 && _hoverIndex < points.Length)
            DrawHover(context, points[_hoverIndex], Labels is { Count: > 0 } labels && _hoverIndex < labels.Count ? labels[_hoverIndex] : values[_hoverIndex].ToString("0.##", CultureInfo.InvariantCulture), w, h);
    }

    private void DrawHover(DrawingContext context, Point point, string text, double w, double h)
    {
        var accent = LineBrush ?? Brushes.White;
        var pen = new Pen(accent, 1);
        context.DrawLine(pen, new Point(point.X, 0), new Point(point.X, h));
        context.DrawEllipse(accent, null, point, 4, 4);

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            Brushes.White);

        const double pad = 8;
        var rectW = ft.Width + pad * 2;
        var rectH = ft.Height + pad * 2;
        var x = Math.Clamp(point.X - rectW / 2, 0, Math.Max(0, w - rectW));
        var y = Math.Clamp(point.Y - rectH - 10, 0, Math.Max(0, h - rectH));
        var rect = new Rect(x, y, rectW, rectH);

        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 20, 20, 20)), null, rect, 6, 6);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1), rect, 6, 6);
        context.DrawText(ft, new Point(x + pad, y + pad));
    }

    /// <summary>Builds a Catmull-Rom smoothed path through the points. When <paramref name="close"/> is set the
    /// path is closed along the bottom edge to form a fillable area.</summary>
    private static Geometry? BuildSmoothGeometry(Point[] points, bool close, double bottom = 0)
    {
        if (points.Length == 0) return null;

        var geo = new StreamGeometry();
        using var ctx = geo.Open();

        ctx.BeginFigure(points[0], close);

        if (points.Length == 1)
        {
            ctx.LineTo(points[0]);
        }
        else
        {
            for (var i = 0; i < points.Length - 1; i++)
            {
                var p0 = points[Math.Max(0, i - 1)];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = points[Math.Min(points.Length - 1, i + 2)];

                var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                ctx.CubicBezierTo(c1, c2, p2);
            }
        }

        if (close)
        {
            ctx.LineTo(new Point(points[^1].X, bottom));
            ctx.LineTo(new Point(points[0].X, bottom));
            ctx.EndFigure(true);
        }
        else
        {
            ctx.EndFigure(false);
        }

        return geo;
    }
}
