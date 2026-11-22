using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AnnotImg;

/// <summary>
/// 画像とアノテーションを描画し、マウス操作で編集するキャンバス。
/// 座標系は画像ピクセル。表示倍率は LayoutTransform で掛ける。
/// </summary>
public sealed class EditorCanvas : Canvas
{
    enum DragMode { None, Create, Move, Resize }

    static readonly Color AccentColor = Color.FromRgb(0x00, 0x78, 0xD7);

    LoadedImage? _image;
    readonly List<Annotation> _items = [];
    readonly Stack<List<Annotation>> _undo = new();
    readonly Stack<List<Annotation>> _redo = new();
    string? _lastMergeKey;

    DragMode _mode;
    Annotation? _dragTarget;
    Annotation? _dragOrig;
    Point _dragStart;
    int _handle;
    bool _dragCheckpointed;
    Annotation? _creating;

    TextBox? _editor;
    Annotation? _editing;
    bool _editingIsNew;
    bool _dirtyBeforeNewText;

    ToolKind _tool = ToolKind.Select;
    double _zoom = 1;
    Brush? _checker;

    public EditorCanvas()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ToolChanged;
    public event EventHandler? DocumentChanged;
    public event EventHandler? ZoomChanged;

    public LoadedImage? Image => _image;
    public bool HasImage => _image != null;

    /// <summary>新しく作るオブジェクトに適用されるスタイル。</summary>
    public Annotation DefaultStyle { get; } = new();

    public Annotation? Selected { get; private set; }
    public bool IsDirty { get; set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public ToolKind Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitEdit();
            _tool = value;
            if (value != ToolKind.Select) SetSelected(null);
            Cursor = CursorForTool();
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>表示倍率。1.0 で画像1ピクセル＝画面1ピクセル。</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.05, 16);
            ApplyScale();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>LayoutTransform に掛かる実際の倍率（DIP 単位）。</summary>
    double Scale => _zoom / DpiScale;

    void ApplyScale()
    {
        LayoutTransform = new ScaleTransform(Scale, Scale);
        if (_editor != null) _editor.BorderThickness = new Thickness(1 / Scale);
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale();
    }

    public void SetImage(LoadedImage img)
    {
        CommitEdit();
        _image = img;
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
        _lastMergeKey = null;
        SetSelected(null);
        IsDirty = false;
        InvalidateMeasure();
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Undo

    List<Annotation> Snapshot() => _items.Select(i => i.Clone()).ToList();

    /// <summary>
    /// 変更前の状態を Undo 履歴に積む。mergeKey が直前と同じなら積まない（スライダー操作などをまとめる）。
    /// </summary>
    public void Checkpoint(string? mergeKey = null)
    {
        if (mergeKey != null && mergeKey == _lastMergeKey) return;
        _undo.Push(Snapshot());
        _redo.Clear();
        _lastMergeKey = mergeKey;
        IsDirty = true;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        CommitEdit();
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        CommitEdit();
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    void Restore(List<Annotation> state)
    {
        _items.Clear();
        _items.AddRange(state);
        _lastMergeKey = null;
        SetSelected(null);
        IsDirty = true;
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>選択中オブジェクトのスタイル変更が終わったら呼ぶ。</summary>
    public void NotifySelectedChanged()
    {
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------- 編集操作

    public void DeleteSelected()
    {
        if (Selected == null) return;
        var target = Selected;
        CommitEdit();
        if (!_items.Contains(target)) return;
        Checkpoint();
        _items.Remove(target);
        SetSelected(null);
        InvalidateVisual();
    }

    public void Nudge(double dx, double dy)
    {
        if (Selected == null || _editing != null) return;
        Checkpoint("nudge:" + Selected.GetHashCode());
        Selected.P1 += new Vector(dx, dy);
        Selected.P2 += new Vector(dx, dy);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        CommitEdit();
        SetSelected(null);
    }

    void SetSelected(Annotation? a)
    {
        if (ReferenceEquals(Selected, a)) return;
        Selected = a;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------- 書き出し

    public BitmapSource RenderToBitmap(bool opaqueBackground)
    {
        if (_image == null) throw new InvalidOperationException("画像が読み込まれていません。");
        CommitEdit();
        var full = new Rect(0, 0, _image.Width, _image.Height);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (opaqueBackground) dc.DrawRectangle(Brushes.White, null, full);
            dc.DrawImage(_image.Bitmap, full);
            foreach (var a in _items) AnnotationRenderer.Render(dc, a, GetBlur);
        }
        var rtb = new RenderTargetBitmap(_image.Width, _image.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // ------------------------------------------------------------- 描画

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        return _image == null ? new Size(0, 0) : new Size(_image.Width, _image.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_image == null) return;
        var full = new Rect(0, 0, _image.Width, _image.Height);
        if (_image.HasAlpha) dc.DrawRectangle(CheckerBrush(), null, full);
        dc.DrawImage(_image.Bitmap, full);

        foreach (var a in _items)
            if (!ReferenceEquals(a, _editing))
                AnnotationRenderer.Render(dc, a, GetBlur);

        if (_creating != null) AnnotationRenderer.Render(dc, _creating, GetBlur);

        if (Selected != null && !ReferenceEquals(Selected, _editing))
            DrawSelection(dc, Selected);
    }

    Brush CheckerBrush()
    {
        if (_checker != null) return _checker;
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var light = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        g.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return _checker = b;
    }

    (BitmapSource?, Rect) GetBlur(Annotation a)
    {
        if (_image == null) return (null, Rect.Empty);
        var b = a.Box;
        int x0 = Math.Clamp((int)Math.Floor(b.Left), 0, _image.Width);
        int y0 = Math.Clamp((int)Math.Floor(b.Top), 0, _image.Height);
        int x1 = Math.Clamp((int)Math.Ceiling(b.Right), 0, _image.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(b.Bottom), 0, _image.Height);
        var r = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        if (r.Width < 1 || r.Height < 1) return (null, Rect.Empty);
        if (a.BlurCache == null || !a.BlurCacheRect.Equals(r))
        {
            a.BlurCache = ImageOps.BlurRegion(_image, r);
            a.BlurCacheRect = r;
        }
        return (a.BlurCache, new Rect(r.X, r.Y, r.Width, r.Height));
    }

    void DrawSelection(DrawingContext dc, Annotation a)
    {
        double s = 1 / Scale;
        var accent = new SolidColorBrush(AccentColor);
        var under = new Pen(Brushes.White, 1 * s);
        var dashed = new Pen(accent, 1 * s) { DashStyle = new DashStyle([4.0, 3.0], 0) };
        if (!a.IsSegment)
        {
            var r = a.Bounds;
            r.Inflate(2 * s, 2 * s);
            dc.DrawRectangle(null, under, r);
            dc.DrawRectangle(null, dashed, r);
        }
        var hp = new Pen(accent, 1 * s);
        foreach (var h in Handles(a))
            dc.DrawRectangle(Brushes.White, hp, new Rect(h.X - 4 * s, h.Y - 4 * s, 8 * s, 8 * s));
    }

    static List<Point> Handles(Annotation a)
    {
        if (a.IsSegment) return [a.P1, a.P2];
        if (a.Kind == AnnotationKind.Text) return [];
        var r = a.Box;
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return
        [
            r.TopLeft, new Point(cx, r.Top), r.TopRight, new Point(r.Right, cy),
            r.BottomRight, new Point(cx, r.Bottom), r.BottomLeft, new Point(r.Left, cy),
        ];
    }

    int HitHandle(Annotation a, Point p)
    {
        double tol = 6 / Scale;
        var hs = Handles(a);
        for (int i = 0; i < hs.Count; i++)
            if (Math.Abs(p.X - hs[i].X) <= tol && Math.Abs(p.Y - hs[i].Y) <= tol) return i;
        return -1;
    }

    Annotation? HitTestItems(Point p)
    {
        double tol = 4 / Scale;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (AnnotationRenderer.HitTest(_items[i], p, tol)) return _items[i];
        return null;
    }

    // ------------------------------------------------------------ マウス

    static AnnotationKind? KindForTool(ToolKind t) => t switch
    {
        ToolKind.Text => AnnotationKind.Text,
        ToolKind.Arrow => AnnotationKind.Arrow,
        ToolKind.Line => AnnotationKind.Line,
        ToolKind.Ellipse => AnnotationKind.Ellipse,
        ToolKind.Rectangle => AnnotationKind.Rectangle,
        ToolKind.Blur => AnnotationKind.Blur,
        _ => null,
    };

    Cursor CursorForTool() => _tool switch
    {
        ToolKind.Select => Cursors.Arrow,
        ToolKind.Text => Cursors.IBeam,
        _ => Cursors.Cross,
    };

    Point ClampToImage(Point p)
        => _image == null ? p : new Point(Math.Clamp(p.X, 0, _image.Width), Math.Clamp(p.Y, 0, _image.Height));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image == null) return;
        CommitEdit();
        Focus();
        var p = e.GetPosition(this);
        e.Handled = true;

        if (_tool == ToolKind.Select)
        {
            if (e.ClickCount == 2 && HitTestItems(p) is { Kind: AnnotationKind.Text } textHit)
            {
                SetSelected(textHit);
                BeginEdit(textHit, false);
                return;
            }
            if (Selected != null && HitHandle(Selected, p) is var h and >= 0)
            {
                StartDrag(DragMode.Resize, Selected, p);
                _handle = h;
                return;
            }
            var hit = HitTestItems(p);
            SetSelected(hit);
            if (hit != null) StartDrag(DragMode.Move, hit, p);
            return;
        }

        if (_tool == ToolKind.Text)
        {
            // 既存の文字列をクリックした場合はそれを編集する
            if (HitTestItems(p) is { Kind: AnnotationKind.Text } existing)
            {
                Tool = ToolKind.Select;
                SetSelected(existing);
                BeginEdit(existing, false);
                return;
            }
            var t = new Annotation { Kind = AnnotationKind.Text, P1 = p, P2 = p };
            t.CopyStyleFrom(DefaultStyle);
            _dirtyBeforeNewText = IsDirty;
            Checkpoint();
            _items.Add(t);
            Tool = ToolKind.Select;
            SetSelected(t);
            BeginEdit(t, true);
            return;
        }

        var kind = KindForTool(_tool)!.Value;
        p = ClampToImage(p);
        _creating = new Annotation { Kind = kind, P1 = p, P2 = p };
        _creating.CopyStyleFrom(DefaultStyle);
        _mode = DragMode.Create;
        _dragStart = p;
        CaptureMouse();
    }

    void StartDrag(DragMode mode, Annotation target, Point p)
    {
        _mode = mode;
        _dragTarget = target;
        _dragOrig = target.Clone();
        _dragStart = p;
        _dragCheckpointed = false;
        CaptureMouse();
    }

    void EnsureDragCheckpoint()
    {
        if (_dragCheckpointed) return;
        Checkpoint();
        _dragCheckpointed = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_image == null) return;
        var p = e.GetPosition(this);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_mode)
        {
            case DragMode.None:
                UpdateHoverCursor(p);
                return;

            case DragMode.Create when _creating != null:
                p = ClampToImage(p);
                _creating.P2 = shift ? Constrain(_creating, _creating.P1, p) : p;
                InvalidateVisual();
                return;

            case DragMode.Move when _dragTarget != null && _dragOrig != null:
            {
                var d = p - _dragStart;
                if (!_dragCheckpointed && d.Length < 1 / Scale) return;
                EnsureDragCheckpoint();
                if (shift) d = Math.Abs(d.X) >= Math.Abs(d.Y) ? new Vector(d.X, 0) : new Vector(0, d.Y);
                _dragTarget.P1 = _dragOrig.P1 + d;
                _dragTarget.P2 = _dragOrig.P2 + d;
                InvalidateVisual();
                return;
            }

            case DragMode.Resize when _dragTarget != null && _dragOrig != null:
                EnsureDragCheckpoint();
                ResizeTo(_dragTarget, _dragOrig, _handle, ClampToImage(p), shift);
                InvalidateVisual();
                return;
        }
    }

    static Point Constrain(Annotation a, Point anchor, Point p)
    {
        var d = p - anchor;
        if (a.IsSegment)
        {
            // 45度刻みにスナップ
            double len = d.Length;
            double ang = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
            return anchor + new Vector(Math.Cos(ang) * len, Math.Sin(ang) * len);
        }
        // 正方形・正円
        double size = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return anchor + new Vector(Math.Sign(d.X == 0 ? 1 : d.X) * size, Math.Sign(d.Y == 0 ? 1 : d.Y) * size);
    }

    static void ResizeTo(Annotation target, Annotation orig, int handle, Point p, bool shift)
    {
        if (orig.IsSegment)
        {
            if (handle == 0) target.P1 = shift ? Constrain(orig, orig.P2, p) : p;
            else target.P2 = shift ? Constrain(orig, orig.P1, p) : p;
            return;
        }
        var r = orig.Box;
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        switch (handle)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: rr = p.X; t = p.Y; break;
            case 3: rr = p.X; break;
            case 4: rr = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        target.P1 = new Point(l, t);
        target.P2 = new Point(rr, b);
    }

    void UpdateHoverCursor(Point p)
    {
        if (_tool != ToolKind.Select)
        {
            Cursor = CursorForTool();
            return;
        }
        if (Selected != null && HitHandle(Selected, p) is var h and >= 0)
        {
            Cursor = Selected.IsSegment
                ? Cursors.Cross
                : h switch
                {
                    0 or 4 => Cursors.SizeNWSE,
                    2 or 6 => Cursors.SizeNESW,
                    1 or 5 => Cursors.SizeNS,
                    _ => Cursors.SizeWE,
                };
            return;
        }
        Cursor = HitTestItems(p) != null ? Cursors.SizeAll : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mode == DragMode.None) return;
        var mode = _mode;
        _mode = DragMode.None;
        ReleaseMouseCapture();

        if (mode == DragMode.Create && _creating != null)
        {
            var a = _creating;
            _creating = null;
            double min = 3 / Scale;
            bool tooSmall = a.IsSegment
                ? (a.P2 - a.P1).Length < min
                : a.Box.Width < min || a.Box.Height < min;
            if (!tooSmall)
            {
                if (a.IsBox) Normalize(a);
                Checkpoint();
                _items.Add(a);
                Tool = ToolKind.Select;
                SetSelected(a);
            }
            InvalidateVisual();
            return;
        }

        if (mode == DragMode.Resize && _dragTarget is { IsBox: true }) Normalize(_dragTarget);
        _dragTarget = null;
        _dragOrig = null;
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mode == DragMode.Create) _creating = null;
        _mode = DragMode.None;
        InvalidateVisual();
    }

    static void Normalize(Annotation a)
    {
        var r = a.Box;
        a.P1 = r.TopLeft;
        a.P2 = r.BottomRight;
    }

    // ------------------------------------------------------- 文字列編集

    public bool IsEditingText => _editing != null;

    void BeginEdit(Annotation a, bool isNew)
    {
        CommitEdit();
        _editing = a;
        _editingIsNew = isNew;
        double b = 1 / Scale;
        var tb = new TextBox
        {
            Text = a.Text,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(a.FontName),
            FontSize = a.FontSize,
            Foreground = new SolidColorBrush(a.StrokeColor),
            CaretBrush = new SolidColorBrush(a.StrokeColor),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(AccentColor),
            BorderThickness = new Thickness(b),
            Padding = new Thickness(0),
            MinWidth = a.FontSize * 0.6,
        };
        // TextBox は内部に左右 2px の余白を持つので、その分ずらして描画位置と揃える
        SetLeft(tb, a.P1.X - 2 - b);
        SetTop(tb, a.P1.Y - b);
        tb.LostKeyboardFocus += (_, _) => CommitEdit();
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                e.Handled = true;
                CommitEdit();
                Focus();
            }
        };
        _editor = tb;
        Children.Add(tb);
        InvalidateVisual();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(_editor, tb)) return;
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
        });
    }

    public void CommitEdit()
    {
        if (_editing == null || _editor == null) return;
        var a = _editing;
        var tb = _editor;
        bool isNew = _editingIsNew;
        _editing = null;
        _editor = null;
        Children.Remove(tb);

        var text = tb.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (isNew)
            {
                // 作成直後に何も入力されなかった場合は、作成自体をなかったことにする
                _items.Remove(a);
                if (_undo.Count > 0) _undo.Pop();
                _lastMergeKey = null;
                IsDirty = _dirtyBeforeNewText;
            }
            else
            {
                Checkpoint();
                _items.Remove(a);
            }
            SetSelected(null);
        }
        else if (text != a.Text)
        {
            if (!isNew) Checkpoint();
            a.Text = text;
        }
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }
}
