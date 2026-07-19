using System.Windows;
using System.Windows.Media;

namespace CodexUsageWidget.Controls;

/// <summary>
/// A lightweight, resolution-independent circular progress indicator.
/// The ring is drawn at render time so it remains circular inside a uniformly scaled Viewbox.
/// </summary>
public sealed class CircularProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            CoercePercentage));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(
            Brushes.DimGray,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush),
        typeof(Brush),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(
            Brushes.MediumSeaGreen,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(
            8d,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            CoerceStrokeThickness));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var thickness = Math.Min(StrokeThickness, Math.Min(ActualWidth, ActualHeight));
        var radius = (Math.Min(ActualWidth, ActualHeight) - thickness) / 2d;
        if (radius <= 0 || TrackBrush is null || ProgressBrush is null)
        {
            return;
        }

        var center = new Point(ActualWidth / 2d, ActualHeight / 2d);
        var trackPen = CreatePen(TrackBrush, thickness);
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var progress = Math.Clamp(Value, 0d, 100d) / 100d;
        if (progress <= 0)
        {
            return;
        }

        var progressPen = CreatePen(ProgressBrush, thickness);
        if (progress >= 0.999999d)
        {
            drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        const double startAngle = -90d;
        var endAngle = startAngle + progress * 360d;
        var startPoint = PointOnCircle(center, radius, startAngle);
        var endPoint = PointOnCircle(center, radius, endAngle);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(startPoint, isFilled: false, isClosed: false);
            context.ArcTo(
                endPoint,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: progress > 0.5d,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, progressPen, geometry);
    }

    private static Pen CreatePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }

    private static object CoercePercentage(DependencyObject _, object value) =>
        value is double number && double.IsFinite(number)
            ? Math.Clamp(number, 0d, 100d)
            : 0d;

    private static object CoerceStrokeThickness(DependencyObject _, object value) =>
        value is double number && double.IsFinite(number)
            ? Math.Max(0d, number)
            : 0d;
}
