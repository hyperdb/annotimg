using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnnotImg;

public enum ToolKind { Select, Text, Arrow, Line, Ellipse, Rectangle, Blur }

public enum AnnotationKind { Text, Arrow, Line, Ellipse, Rectangle, Blur }

public enum LineStyle { Solid, Dot, Dash }

/// <summary>
/// 画像上に配置される1つのオブジェクト。座標はすべて画像のピクセル座標。
/// </summary>
public sealed class Annotation
{
    public AnnotationKind Kind { get; set; }

    /// <summary>線・矢印では始点、それ以外は矩形の一方の角（文字列では左上）。</summary>
    public Point P1 { get; set; }

    /// <summary>線・矢印では終点（矢印の先端）、それ以外は矩形の対角。</summary>
    public Point P2 { get; set; }

    // --- スタイル ---
    public Color StrokeColor { get; set; } = Color.FromRgb(0xE5, 0x1A, 0x1A);
    public double StrokeThickness { get; set; } = 4;
    public LineStyle LineStyle { get; set; } = LineStyle.Solid;
    public Color FillColor { get; set; } = Color.FromRgb(0xFF, 0xD8, 0x00);
    public double FillOpacity { get; set; } = 0;
    public bool Rounded { get; set; }
    public string Text { get; set; } = "";
    public string FontName { get; set; } = "Yu Gothic UI";
    public double FontSize { get; set; } = 32;

    // --- ぼかし結果のキャッシュ ---
    internal BitmapSource? BlurCache;
    internal Int32Rect BlurCacheRect;

    public Annotation Clone() => (Annotation)MemberwiseClone();

    public void CopyStyleFrom(Annotation s)
    {
        StrokeColor = s.StrokeColor;
        StrokeThickness = s.StrokeThickness;
        LineStyle = s.LineStyle;
        FillColor = s.FillColor;
        FillOpacity = s.FillOpacity;
        Rounded = s.Rounded;
        FontName = s.FontName;
        FontSize = s.FontSize;
    }

    public bool IsSegment => Kind is AnnotationKind.Arrow or AnnotationKind.Line;

    public bool IsBox => Kind is AnnotationKind.Ellipse or AnnotationKind.Rectangle or AnnotationKind.Blur;

    /// <summary>P1/P2 から作られる正規化済み矩形。</summary>
    public Rect Box => new(P1, P2);

    public FormattedText CreateFormattedText()
    {
        var text = string.IsNullOrEmpty(Text) ? " " : Text;
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            FontSize,
            new SolidColorBrush(StrokeColor),
            1.0);
    }

    /// <summary>選択枠の表示などに使う外接矩形。</summary>
    public Rect Bounds
    {
        get
        {
            if (Kind == AnnotationKind.Text)
            {
                var ft = CreateFormattedText();
                return new Rect(P1.X, P1.Y, Math.Max(ft.WidthIncludingTrailingWhitespace, 1), ft.Height);
            }
            return Box;
        }
    }
}
