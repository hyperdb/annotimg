using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnnotImg;

/// <summary>
/// アノテーションの描画とヒットテスト。画面表示と保存時の書き出しで同じ処理を使う。
/// </summary>
public static class AnnotationRenderer
{
    public delegate (BitmapSource? Bitmap, Rect Rect) BlurProvider(Annotation a);

    public static Pen MakePen(Color color, double thickness, LineStyle style, bool roundCaps)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            LineJoin = roundCaps ? PenLineJoin.Round : PenLineJoin.Miter,
            StartLineCap = roundCaps ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = roundCaps ? PenLineCap.Round : PenLineCap.Flat,
        };
        switch (style)
        {
            case LineStyle.Dot:
                // 長さ0の線分＋丸キャップで丸い点を並べる（単位は線の太さ）
                pen.DashStyle = new DashStyle([0.0, 2.0], 0);
                pen.DashCap = PenLineCap.Round;
                break;
            case LineStyle.Dash:
                pen.DashStyle = new DashStyle([3.0, 2.0], 0);
                pen.DashCap = PenLineCap.Flat;
                break;
        }
        pen.Freeze();
        return pen;
    }

    public static double CornerRadius(Rect r, double thickness)
        => Math.Min(Math.Min(r.Width, r.Height) / 4, 16 + thickness * 2);

    static Brush? FillBrush(Annotation a)
    {
        if (a.FillOpacity <= 0.001) return null;
        var b = new SolidColorBrush(a.FillColor) { Opacity = Math.Clamp(a.FillOpacity, 0, 1) };
        b.Freeze();
        return b;
    }

    public static void Render(DrawingContext dc, Annotation a, BlurProvider blur)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Line:
            {
                var pen = MakePen(a.StrokeColor, Math.Max(1, a.StrokeThickness), a.LineStyle, true);
                dc.DrawLine(pen, a.P1, a.P2);
                break;
            }
            case AnnotationKind.Arrow:
                DrawArrow(dc, a);
                break;
            case AnnotationKind.Rectangle:
            {
                var r = a.Box;
                var pen = a.StrokeThickness > 0 ? MakePen(a.StrokeColor, a.StrokeThickness, a.LineStyle, a.Rounded) : null;
                double rad = a.Rounded ? CornerRadius(r, a.StrokeThickness) : 0;
                dc.DrawRoundedRectangle(FillBrush(a), pen, r, rad, rad);
                break;
            }
            case AnnotationKind.Ellipse:
            {
                var r = a.Box;
                var pen = a.StrokeThickness > 0 ? MakePen(a.StrokeColor, a.StrokeThickness, a.LineStyle, true) : null;
                dc.DrawEllipse(FillBrush(a), pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            }
            case AnnotationKind.Blur:
            {
                var (bmp, rect) = blur(a);
                if (bmp != null) dc.DrawImage(bmp, rect);
                break;
            }
            case AnnotationKind.Text:
                if (!string.IsNullOrEmpty(a.Text))
                    dc.DrawText(a.CreateFormattedText(), a.P1);
                break;
        }
    }

    static (double Head, double Half) ArrowHeadSize(double thickness, double length)
    {
        double head = Math.Min(length, Math.Max(12, thickness * 3.5));
        return (head, head * 0.5);
    }

    static void DrawArrow(DrawingContext dc, Annotation a)
    {
        double t = Math.Max(1, a.StrokeThickness);
        Vector v = a.P2 - a.P1;
        double len = v.Length;
        if (len < 0.5) return;
        v /= len;
        var n = new Vector(-v.Y, v.X);
        var (head, half) = ArrowHeadSize(t, len);

        Point b = a.P2 - v * head;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a.P2, true, true);
            g.LineTo(b + n * half, true, true);
            g.LineTo(b - n * half, true, true);
        }
        geo.Freeze();

        // 軸は矢じりの少し内側で止める（先端が太い線で丸まらないように）
        Point shaftEnd = a.P2 - v * (head * 0.8);
        if (Vector.Multiply(shaftEnd - a.P1, v) > 0)
        {
            var pen = MakePen(a.StrokeColor, t, a.LineStyle, true);
            dc.DrawLine(pen, a.P1, shaftEnd);
        }
        var brush = new SolidColorBrush(a.StrokeColor);
        brush.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double l2 = ab.LengthSquared;
        if (l2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <param name="tol">許容誤差（画像ピクセル単位）</param>
    public static bool HitTest(Annotation a, Point p, double tol)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Line:
                return DistanceToSegment(p, a.P1, a.P2) <= Math.Max(1, a.StrokeThickness) / 2 + tol;
            case AnnotationKind.Arrow:
            {
                double t = Math.Max(1, a.StrokeThickness);
                var (_, half) = ArrowHeadSize(t, (a.P2 - a.P1).Length);
                double d = DistanceToSegment(p, a.P1, a.P2);
                return d <= t / 2 + tol || ((p - a.P2).Length <= half * 2 && d <= half + tol);
            }
            case AnnotationKind.Rectangle:
            case AnnotationKind.Ellipse:
            {
                var r = a.Box;
                Geometry geo = a.Kind == AnnotationKind.Ellipse
                    ? new EllipseGeometry(r)
                    : new RectangleGeometry(r, a.Rounded ? CornerRadius(r, a.StrokeThickness) : 0, a.Rounded ? CornerRadius(r, a.StrokeThickness) : 0);
                if (a.FillOpacity > 0.001 && geo.FillContains(p)) return true;
                return geo.StrokeContains(new Pen(Brushes.Black, a.StrokeThickness + tol * 2), p);
            }
            case AnnotationKind.Blur:
                return a.Box.Contains(p);
            case AnnotationKind.Text:
            {
                var r = a.Bounds;
                r.Inflate(tol / 2, tol / 2);
                return r.Contains(p);
            }
        }
        return false;
    }
}
