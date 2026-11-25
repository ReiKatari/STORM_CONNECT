using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;

namespace StormConnect.Desktop.Controls;

/// <summary>
/// Animated 60 FPS dual-series spline & area telemetry chart (FPS & Bandwidth Mbps)
/// with glowing live head markers and floating hover tooltip.
/// </summary>
public sealed class LiveChartControl : Canvas
{
    private readonly Queue<double> _fpsHistory = new();
    private readonly Queue<double> _mbpsHistory = new();
    private const int MaxPoints = 40;
    private Point? _hoverPos;

    public LiveChartControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Redraw();
        MouseMove += (_, e) => { _hoverPos = e.GetPosition(this); Redraw(); };
        MouseLeave += (_, _) => { _hoverPos = null; Redraw(); };

        for (int i = 0; i < 12; i++)
        {
            _fpsHistory.Enqueue(0);
            _mbpsHistory.Enqueue(0);
        }
    }

    public void PushSample(double fps, double mbps)
    {
        _fpsHistory.Enqueue(fps);
        _mbpsHistory.Enqueue(mbps);
        while (_fpsHistory.Count > MaxPoints) _fpsHistory.Dequeue();
        while (_mbpsHistory.Count > MaxPoints) _mbpsHistory.Dequeue();
        Redraw();
    }

    public void Redraw()
    {
        Children.Clear();
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        var borderBrush = TryFindResource("ThemeCardBorder") as Brush ?? Brushes.DimGray;
        var accentBrush = TryFindResource("ThemeAccent") as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(139, 92, 246));
        var accent2Brush = TryFindResource("ThemeAccentSecondary") as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(6, 182, 212));
        var textBrush = TryFindResource("ThemeTextSecondary") as Brush ?? Brushes.LightGray;

        // Subtle horizontal grid lines
        for (int i = 1; i <= 3; i++)
        {
            double gy = h * i / 4.0;
            Children.Add(new Line
            {
                X1 = 0, Y1 = gy, X2 = w, Y2 = gy,
                Stroke = borderBrush,
                StrokeThickness = 0.6,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                Opacity = 0.55
            });
        }

        var fpsArr = _fpsHistory.ToArray();
        var mbpsArr = _mbpsHistory.ToArray();
        double maxFps = Math.Max(60.0, fpsArr.Length > 0 ? fpsArr.Max() * 1.15 : 60.0);
        double maxMbps = Math.Max(10.0, mbpsArr.Length > 0 ? mbpsArr.Max() * 1.2 : 10.0);

        DrawSeries(fpsArr, maxFps, accentBrush.Color, w, h, true);
        DrawSeries(mbpsArr, maxMbps, accent2Brush.Color, w, h, false);

        // Hover marker & tooltip
        if (_hoverPos is Point hp && fpsArr.Length > 1)
        {
            int idx = Math.Clamp((int)Math.Round(hp.X / w * (fpsArr.Length - 1)), 0, fpsArr.Length - 1);
            double px = idx * (w / Math.Max(1, fpsArr.Length - 1));
            Children.Add(new Line
            {
                X1 = px, Y1 = 4, X2 = px, Y2 = h - 4,
                Stroke = textBrush,
                StrokeThickness = 1,
                Opacity = 0.5
            });

            var tipBorder = new Border
            {
                Background = TryFindResource("ThemeHeaderBg") as Brush ?? Brushes.Black,
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Child = new TextBlock
                {
                    Text = $"{fpsArr[idx]:0.#} FPS  |  {mbpsArr[idx]:0.##} Mbps",
                    Foreground = TryFindResource("ThemeTextPrimary") as Brush ?? Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                }
            };
            SetLeft(tipBorder, Math.Clamp(px + 8, 4, Math.Max(4, w - 155)));
            SetTop(tipBorder, 6);
            Children.Add(tipBorder);
        }
    }

    private void DrawSeries(double[] values, double maxVal, Color color, double w, double h, bool fillArea)
    {
        if (values.Length < 2) return;
        double padY = 10;
        double usableH = Math.Max(10, h - padY * 2);
        double stepX = w / Math.Max(1, values.Length - 1);

        var pts = new Point[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double norm = Math.Clamp(values[i] / maxVal, 0, 1);
            pts[i] = new Point(i * stepX, h - padY - norm * usableH);
        }

        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false };
        for (int i = 1; i < pts.Length; i++)
        {
            var prev = pts[i - 1];
            var cur = pts[i];
            double midX = (prev.X + cur.X) / 2.0;
            fig.Segments.Add(new BezierSegment(new Point(midX, prev.Y), new Point(midX, cur.Y), cur, true));
        }

        if (fillArea)
        {
            var areaFig = new PathFigure { StartPoint = new Point(pts[0].X, h), IsClosed = true };
            areaFig.Segments.Add(new LineSegment(pts[0], false));
            for (int i = 1; i < pts.Length; i++)
            {
                var prev = pts[i - 1];
                var cur = pts[i];
                double midX = (prev.X + cur.X) / 2.0;
                areaFig.Segments.Add(new BezierSegment(new Point(midX, prev.Y), new Point(midX, cur.Y), cur, false));
            }
            areaFig.Segments.Add(new LineSegment(new Point(pts[^1].X, h), false));

            var areaGeo = new PathGeometry([areaFig]);
            Children.Add(new Path
            {
                Data = areaGeo,
                Fill = new LinearGradientBrush(Color.FromArgb(85, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90)
            });
        }

        var strokeGeo = new PathGeometry([fig]);
        Children.Add(new Path
        {
            Data = strokeGeo,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        // Glowing live head marker
        var last = pts[^1];
        var halo = new Ellipse
        {
            Width = 12, Height = 12,
            Fill = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B))
        };
        SetLeft(halo, last.X - 6);
        SetTop(halo, last.Y - 6);
        Children.Add(halo);

        var dot = new Ellipse
        {
            Width = 6, Height = 6,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5
        };
        SetLeft(dot, last.X - 3);
        SetTop(dot, last.Y - 3);
        Children.Add(dot);
    }
}
