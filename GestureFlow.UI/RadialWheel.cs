using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GestureFlow.Core;
using GestureFlow.Profiles;

namespace GestureFlow.UI;

public sealed class RadialWheel : FrameworkElement
{
    private static readonly Typeface LabelFont = new("Microsoft YaHei UI");
    private static readonly Typeface ActiveFont = new(LabelFont.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush RingFill = MakeBrush(Color.FromRgb(247, 246, 241));
    private static readonly Brush SelectedFill = MakeBrush(Color.FromRgb(190, 191, 190));
    private static readonly Brush Ink = MakeBrush(BrandColors.Graphite);
    private static readonly Brush Rule = MakeBrush(Color.FromRgb(198, 197, 191));
    private static readonly Brush DarkRule = MakeBrush(Color.FromRgb(177, 176, 170));

    public static readonly DependencyProperty ShowShortcutsProperty = DependencyProperty.Register(
        nameof(ShowShortcuts), typeof(bool), typeof(RadialWheel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(
        nameof(IsDark), typeof(bool), typeof(RadialWheel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CancelRadiusProperty = DependencyProperty.Register(
        nameof(CancelRadius), typeof(double), typeof(RadialWheel),
        new FrameworkPropertyMetadata(GestureWheelMetrics.CenterRadius, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double distance && double.IsFinite(distance) && distance > 0
            && distance < GestureWheelMetrics.OuterDiameter / 2.0);

    private GestureProfile? _profile;
    private GestureDirection? _highlight;
    private Point? _anchor;

    public bool ShowShortcuts
    {
        get => (bool)GetValue(ShowShortcutsProperty);
        set => SetValue(ShowShortcutsProperty, value);
    }

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public double CancelRadius
    {
        get => (double)GetValue(CancelRadiusProperty);
        set => SetValue(CancelRadiusProperty, value);
    }

    public double RenderedRadius => Math.Max(0, Math.Min(GestureWheelMetrics.OuterDiameter / 2.0,
        Math.Min(ActualWidth, ActualHeight) / 2 - 2));

    public double RenderedCenterRadius => CancelRadius * RenderedRadius / (GestureWheelMetrics.OuterDiameter / 2.0);

    public void Show(GestureProfile profile, GestureDirection? highlight = null, Point? anchor = null)
    {
        if (ReferenceEquals(_profile, profile) && _highlight == highlight && _anchor == anchor)
            return;

        _profile = profile;
        _highlight = highlight;
        _anchor = anchor;
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var radius = RenderedRadius;
        if (_profile is null || radius < 30)
            return;

        var center = _anchor is { } point
            ? new Point(Math.Clamp(point.X, radius, ActualWidth - radius),
                Math.Clamp(point.Y, radius, ActualHeight - radius))
            : new Point(ActualWidth / 2, ActualHeight / 2);
        var inner = RenderedCenterRadius;
        var step = Math.Tau / (int)_profile.Mode;
        var annulus = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, radius, radius), new EllipseGeometry(center, inner, inner));
        drawing.DrawGeometry(RingFill, null, annulus);

        if (_highlight is { } highlighted && _profile.GetEnabledBinding(highlighted) is not null)
        {
            var angle = (int)highlighted * Math.Tau / 8;
            drawing.DrawGeometry(SelectedFill, null, Sector(center, radius, inner, angle, step));
        }

        var line = IsDark ? DarkRule : Rule;
        var ring = new Pen(line, GestureWheelMetrics.OutlineWidth);
        var divider = new Pen(line, GestureWheelMetrics.DividerWidth);
        drawing.DrawEllipse(null, ring, center, radius, radius);
        drawing.DrawEllipse(null, ring, center, inner, inner);

        foreach (var direction in DirectionRecognizer.GetDirections(_profile.Mode))
        {
            var angle = (int)direction * Math.Tau / 8;
            var boundary = angle + step / 2;
            var from = At(center, inner, boundary);
            var to = At(center, radius, boundary);
            drawing.DrawLine(divider, from, to);
        }

        var labelRadius = (radius + inner) / 2;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var binding in _profile.Bindings)
        {
            var angle = (int)binding.Direction * Math.Tau / 8;
            var selected = binding.Direction == _highlight && binding.Enabled;
            var labelCenter = At(center, labelRadius, angle);
            var radialWidth = radius - inner;
            var tangentWidth = 2 * labelRadius * Math.Sin(step / 2);
            var width = Math.Max(20, Math.Min(
                tangentWidth / Math.Max(0.01, Math.Abs(Math.Cos(angle))),
                radialWidth / Math.Max(0.01, Math.Abs(Math.Sin(angle)))) - 5);
            var shortcut = ShowShortcuts && radialWidth >= 38;
            var label = binding.DisplayName.Replace('\n', ' ').Replace('\r', ' ');
            DrawText(drawing, label, labelCenter + new Vector(0, shortcut ? -6 : 0),
                selected ? ActiveFont : LabelFont, GestureWheelMetrics.LabelSize, width, dpi);
            if (shortcut)
                DrawText(drawing, GestureLabels.Shortcut(binding.Shortcut), labelCenter + new Vector(0, 8),
                    LabelFont, 10, width, dpi);
        }
    }

    private static Geometry Sector(Point center, double radius, double inner, double angle, double step)
    {
        var start = angle - step / 2;
        var end = angle + step / 2;
        var sector = new StreamGeometry();
        using (var path = sector.Open())
        {
            path.BeginFigure(At(center, radius, start), true, true);
            path.ArcTo(At(center, radius, end), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(At(center, inner, end), true, false);
            path.ArcTo(At(center, inner, start), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        sector.Freeze();
        return sector;
    }

    private static Point At(Point center, double radius, double angle) =>
        center + new Vector(radius * Math.Sin(angle), -radius * Math.Cos(angle));

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void DrawText(DrawingContext drawing, string label, Point center, Typeface font,
        double size, double width, double dpi)
    {
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, size, Ink, dpi)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var origin = new Point(center.X - width / 2, center.Y - text.Height / 2);
        drawing.DrawText(text, origin);
    }
}
