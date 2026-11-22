using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AnnotImg;

public sealed record FontItem(string DisplayName, string Source);

public partial class MainWindow : Window
{
    static readonly Color[] Palette =
    [
        Color.FromRgb(0xE5, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x8C, 0x00), Color.FromRgb(0xFF, 0xD8, 0x00),
        Color.FromRgb(0x22, 0xB1, 0x4C), Color.FromRgb(0x00, 0x78, 0xD7), Color.FromRgb(0xE3, 0x00, 0x8C),
        Colors.Black, Color.FromRgb(0x80, 0x80, 0x80), Colors.White,
    ];

    // InitializeComponent 中に発生する ValueChanged 等を無視するため true で開始し、LoadProps の終わりで解除する
    bool _loadingProps = true;
    readonly int[] _customColors = new int[16];

    public MainWindow()
    {
        InitializeComponent();

        Editor.SelectionChanged += (_, _) => LoadProps();
        Editor.ToolChanged += (_, _) => { SyncToolButtons(); LoadProps(); };
        Editor.DocumentChanged += (_, _) => UpdateUi();
        Editor.ZoomChanged += (_, _) => ZoomText.Text = Editor.HasImage ? $"{Editor.Zoom * 100:0}%" : "";

        BuildPalette(StrokePalette, c => ApplyStyle("stroke", a => a.StrokeColor = c, true));
        BuildPalette(FillPalette, c =>
        {
            ApplyStyle("fill", a =>
            {
                a.FillColor = c;
                // 塗りなしの状態で色を選んだら、塗りが見えるように不透明度を上げる
                if (a.FillOpacity <= 0.001) a.FillOpacity = 0.5;
            }, true);
        });
        LoadFonts();
        LoadProps();
        UpdateUi();
    }

    // ------------------------------------------------------------ 初期化

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) OpenFile(args[1]);
    }

    void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        // ツールバー右端のオーバーフローボタンを隠す
        if (sender is ToolBar tb && tb.Template.FindName("OverflowGrid", tb) is FrameworkElement overflow)
            overflow.Visibility = Visibility.Collapsed;
    }

    void BuildPalette(Panel panel, Action<Color> onPick)
    {
        foreach (var c in Palette)
        {
            var b = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0),
                Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}", Focusable = false,
            };
            b.Click += (_, _) => onPick(c);
            panel.Children.Add(b);
        }
    }

    void LoadFonts()
    {
        var ja = XmlLanguage.GetLanguage("ja-jp");
        var items = Fonts.SystemFontFamilies
            .Select(f => new FontItem(
                f.FamilyNames.TryGetValue(ja, out var n) ? n : f.Source,
                f.Source))
            .DistinctBy(f => f.Source)
            .OrderBy(f => f.DisplayName, StringComparer.CurrentCulture)
            .ToList();
        FontCombo.ItemsSource = items;

        // 既定フォント（游ゴシック UI → メイリオ → Segoe UI の順で存在するものを採用）
        foreach (var name in new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "Segoe UI" })
        {
            if (items.Any(i => i.Source == name))
            {
                Editor.DefaultStyle.FontName = name;
                break;
            }
        }
    }

    // ------------------------------------------------------------ 状態表示

    void UpdateUi()
    {
        bool has = Editor.HasImage;
        Placeholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = has;
        UndoButton.IsEnabled = Editor.CanUndo;
        RedoButton.IsEnabled = Editor.CanRedo;
        DeleteButton.IsEnabled = Editor.Selected != null;

        if (Editor.Image is { } img)
        {
            var name = Path.GetFileName(img.Path);
            Title = $"{(Editor.IsDirty ? "● " : "")}{name} - AnnotImg";
            StatusText.Text = $"{img.Path}    {img.Width} × {img.Height} px";
        }
        else
        {
            Title = "AnnotImg";
        }
    }

    void SyncToolButtons()
    {
        RadioButton rb = Editor.Tool switch
        {
            ToolKind.Text => ToolText,
            ToolKind.Arrow => ToolArrow,
            ToolKind.Line => ToolLine,
            ToolKind.Ellipse => ToolEllipse,
            ToolKind.Rectangle => ToolRect,
            ToolKind.Blur => ToolBlur,
            _ => ToolSelect,
        };
        rb.IsChecked = true;
    }

    void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (Editor == null) return;
        if (sender is RadioButton { Tag: ToolKind t }) Editor.Tool = t;
    }

    // --------------------------------------------------------- プロパティ

    AnnotationKind? CurrentKind()
    {
        if (Editor.Selected != null) return Editor.Selected.Kind;
        return Editor.Tool switch
        {
            ToolKind.Text => AnnotationKind.Text,
            ToolKind.Arrow => AnnotationKind.Arrow,
            ToolKind.Line => AnnotationKind.Line,
            ToolKind.Ellipse => AnnotationKind.Ellipse,
            ToolKind.Rectangle => AnnotationKind.Rectangle,
            ToolKind.Blur => AnnotationKind.Blur,
            _ => null,
        };
    }

    static string KindName(AnnotationKind k) => k switch
    {
        AnnotationKind.Text => "文字",
        AnnotationKind.Arrow => "矢印",
        AnnotationKind.Line => "線",
        AnnotationKind.Ellipse => "円",
        AnnotationKind.Rectangle => "四角形",
        AnnotationKind.Blur => "ぼかし",
        _ => "",
    };

    /// <summary>選択中のオブジェクト（なければ既定スタイル）の値をパネルに反映する。</summary>
    void LoadProps()
    {
        var src = Editor.Selected ?? Editor.DefaultStyle;
        var kind = CurrentKind();
        _loadingProps = true;
        try
        {
            PropsHeader.Text = kind is { } k
                ? (Editor.Selected != null ? $"選択中: {KindName(k)}" : $"新規: {KindName(k)}")
                : "スタイル（既定値）";

            static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
            bool all = kind == null;
            GroupColor.Visibility = V(all || kind != AnnotationKind.Blur);
            GroupThickness.Visibility = V(all || kind is AnnotationKind.Arrow or AnnotationKind.Line or AnnotationKind.Ellipse or AnnotationKind.Rectangle);
            GroupLineStyle.Visibility = GroupThickness.Visibility;
            GroupFill.Visibility = V(all || kind is AnnotationKind.Ellipse or AnnotationKind.Rectangle);
            GroupRounded.Visibility = V(all || kind is AnnotationKind.Rectangle);
            GroupFont.Visibility = V(all || kind is AnnotationKind.Text);
            GroupBlur.Visibility = V(kind is AnnotationKind.Blur);
            ColorLabel.Text = kind switch
            {
                AnnotationKind.Text => "文字の色",
                null => "線・文字の色",
                _ => "線の色",
            };
            // 線・矢印は太さ0にできない
            ThicknessSlider.Minimum = kind is AnnotationKind.Arrow or AnnotationKind.Line ? 1 : 0;

            StrokeSwatch.Background = new SolidColorBrush(src.StrokeColor);
            FillSwatch.Background = new SolidColorBrush(src.FillColor);
            ThicknessSlider.Value = src.StrokeThickness;
            ThicknessText.Text = src.StrokeThickness.ToString("0");
            LineStyleCombo.SelectedIndex = (int)src.LineStyle;
            FillOpacitySlider.Value = Math.Round(src.FillOpacity * 100);
            FillOpacityText.Text = Math.Round(src.FillOpacity * 100).ToString("0");
            RoundedCheck.IsChecked = src.Rounded;
            FontCombo.SelectedItem = (FontCombo.ItemsSource as IEnumerable<FontItem>)?.FirstOrDefault(f => f.Source == src.FontName);
            FontSizeSlider.Value = src.FontSize;
            FontSizeText.Text = src.FontSize.ToString("0");
        }
        finally
        {
            _loadingProps = false;
        }
        UpdateUi();
    }

    /// <summary>スタイル変更を既定スタイルと選択中オブジェクトに適用する。</summary>
    void ApplyStyle(string key, Action<Annotation> apply, bool reload = false)
    {
        if (_loadingProps) return;
        apply(Editor.DefaultStyle);
        var sel = Editor.Selected;
        if (sel != null)
        {
            Editor.Checkpoint($"style:{key}:{sel.GetHashCode()}");
            apply(sel);
            Editor.NotifySelectedChanged();
        }
        if (reload) LoadProps();
    }

    void StrokeColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (Editor.Selected ?? Editor.DefaultStyle).StrokeColor;
        if (PickColor(current) is { } c) ApplyStyle("stroke", a => a.StrokeColor = c, true);
    }

    void FillColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (Editor.Selected ?? Editor.DefaultStyle).FillColor;
        if (PickColor(current) is { } c)
        {
            ApplyStyle("fill", a =>
            {
                a.FillColor = c;
                if (a.FillOpacity <= 0.001) a.FillOpacity = 0.5;
            }, true);
        }
    }

    Color? PickColor(Color current)
    {
        Editor.CommitEdit();
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            CustomColors = _customColors,
        };
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dlg.ShowDialog(new Win32Owner(owner)) != System.Windows.Forms.DialogResult.OK) return null;
        Array.Copy(dlg.CustomColors, _customColors, Math.Min(16, dlg.CustomColors.Length));
        return Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
    }

    sealed class Win32Owner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessText == null) return;
        ThicknessText.Text = e.NewValue.ToString("0");
        ApplyStyle("thickness", a => a.StrokeThickness = e.NewValue);
    }

    void LineStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LineStyleCombo.SelectedItem is ComboBoxItem { Tag: LineStyle s })
            ApplyStyle("linestyle", a => a.LineStyle = s);
    }

    void FillOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FillOpacityText == null) return;
        FillOpacityText.Text = e.NewValue.ToString("0");
        ApplyStyle("fillopacity", a => a.FillOpacity = e.NewValue / 100.0);
    }

    void Rounded_Changed(object sender, RoutedEventArgs e)
    {
        bool v = RoundedCheck.IsChecked == true;
        ApplyStyle("rounded", a => a.Rounded = v);
    }

    void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is FontItem f)
            ApplyStyle("font", a => a.FontName = f.Source);
    }

    void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeText == null) return;
        FontSizeText.Text = e.NewValue.ToString("0");
        ApplyStyle("fontsize", a => a.FontSize = e.NewValue);
    }

    // ---------------------------------------------------------- ファイル

    bool ConfirmDiscard()
    {
        Editor.CommitEdit();
        if (!Editor.HasImage || !Editor.IsDirty) return true;
        var r = MessageBox.Show(this, "編集内容が保存されていません。保存しますか？", "AnnotImg",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return r switch
        {
            MessageBoxResult.Yes => SaveAs(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    void Open_Click(object sender, RoutedEventArgs e) => OpenWithDialog();

    void OpenWithDialog()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog
        {
            Title = "画像を開く",
            Filter = "画像ファイル (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|すべてのファイル (*.*)|*.*",
        };
        if (Editor.Image is { } img) dlg.InitialDirectory = Path.GetDirectoryName(img.Path);
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    void OpenFile(string path)
    {
        if (!ImageOps.IsSupported(path))
        {
            MessageBox.Show(this, "PNG または JPEG ファイルを指定してください。", "AnnotImg", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        LoadedImage img;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            img = ImageOps.Load(path);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, $"画像を開けませんでした。\n{ex.Message}", "AnnotImg", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Mouse.OverrideCursor = null;
        Editor.Tool = ToolKind.Select;
        Editor.SetImage(img);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ZoomToFit);
        Editor.Focus();
    }

    void Save_Click(object sender, RoutedEventArgs e) => SaveAs();

    bool SaveAs()
    {
        if (Editor.Image is not { } img) return false;
        Editor.CommitEdit();
        var dir = Path.GetDirectoryName(img.Path);
        var baseName = Path.GetFileNameWithoutExtension(img.Path);
        var ext = Path.GetExtension(img.Path).ToLowerInvariant();
        var dlg = new SaveFileDialog
        {
            Title = "名前を付けて保存",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg;*.jpeg|WebP (*.webp)|*.webp",
            FilterIndex = ext is ".jpg" or ".jpeg" ? 2 : 1,
            FileName = baseName + "_annotated" + (ext is ".jpg" or ".jpeg" ? ".jpg" : ".png"),
            InitialDirectory = dir,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return false;

        var path = dlg.FileName;
        var outExt = Path.GetExtension(path).ToLowerInvariant();
        if (outExt is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            path += dlg.FilterIndex switch { 2 => ".jpg", 3 => ".webp", _ => ".png" };
            outExt = Path.GetExtension(path);
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var bmp = Editor.RenderToBitmap(opaqueBackground: outExt is ".jpg" or ".jpeg");
            ImageOps.Save(bmp, path);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, $"保存に失敗しました。\n{ex.Message}", "AnnotImg", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        Mouse.OverrideCursor = null;
        Editor.IsDirty = false;
        UpdateUi();
        StatusText.Text = $"保存しました: {path}";
        return true;
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    // ---------------------------------------------------- ドラッグ＆ドロップ

    static string? DroppedFile(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            ? files[0]
            : null;

    void Window_DragOver(object sender, DragEventArgs e)
    {
        var f = DroppedFile(e);
        e.Effects = f != null && ImageOps.IsSupported(f) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        var f = DroppedFile(e);
        if (f == null) return;
        e.Handled = true;
        Activate();
        // ドロップ処理中にダイアログを出すとエクスプローラーが待たされるので後で処理する
        Dispatcher.BeginInvoke(() =>
        {
            if (ConfirmDiscard()) OpenFile(f);
        });
    }

    // ------------------------------------------------------------ 編集操作

    void Undo_Click(object sender, RoutedEventArgs e) => Editor.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => Editor.Redo();
    void Delete_Click(object sender, RoutedEventArgs e) => Editor.DeleteSelected();

    // ------------------------------------------------------------------ 表示倍率

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomToFit();
    void Zoom100_Click(object sender, RoutedEventArgs e) => Editor.Zoom = 1;

    void ZoomBy(double factor)
    {
        if (!Editor.HasImage) return;
        Editor.Zoom *= factor;
    }

    void ZoomToFit()
    {
        if (Editor.Image is not { } img) return;
        Scroller.UpdateLayout();
        double aw = Math.Max(50, Scroller.ActualWidth - 60);
        double ah = Math.Max(50, Scroller.ActualHeight - 60);
        double dpi = Editor.DpiScale;
        double fit = Math.Min(aw * dpi / img.Width, ah * dpi / img.Height);
        Editor.Zoom = Math.Min(1, fit);
    }

    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
    }

    // ------------------------------------------------------------ キーボード

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // ファイル操作は文字入力中でも有効
        if (ctrl && key == Key.O) { e.Handled = true; OpenWithDialog(); return; }
        if (ctrl && key == Key.S) { e.Handled = true; SaveAs(); return; }

        // 文字入力中はテキストボックスに任せる
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: Editor.Redo(); e.Handled = true; return;
                case Key.Z: Editor.Undo(); e.Handled = true; return;
                case Key.Y: Editor.Redo(); e.Handled = true; return;
                case Key.D0 or Key.NumPad0: ZoomToFit(); e.Handled = true; return;
                case Key.D1 or Key.NumPad1: Editor.Zoom = 1; e.Handled = true; return;
                case Key.OemPlus or Key.Add: ZoomBy(1.25); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25); e.Handled = true; return;
            }
            return;
        }

        // スライダー等を操作中は矢印キーをそちらに渡す
        bool inPanel = PropsPanel.IsKeyboardFocusWithin;

        switch (key)
        {
            case Key.Delete or Key.Back when Editor.Selected != null:
                Editor.DeleteSelected(); e.Handled = true; return;
            case Key.Escape:
                if (Editor.Tool != ToolKind.Select) Editor.Tool = ToolKind.Select;
                else Editor.ClearSelection();
                e.Handled = true;
                return;
            case Key.Left or Key.Right or Key.Up or Key.Down when Editor.Selected != null && !inPanel:
            {
                double step = shift ? 10 : 1;
                double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                Editor.Nudge(dx, dy);
                e.Handled = true;
                return;
            }
        }

        if (mods != ModifierKeys.None || !Editor.HasImage) return;
        ToolKind? tool = key switch
        {
            Key.V => ToolKind.Select,
            Key.T => ToolKind.Text,
            Key.A => ToolKind.Arrow,
            Key.L => ToolKind.Line,
            Key.E => ToolKind.Ellipse,
            Key.R => ToolKind.Rectangle,
            Key.B => ToolKind.Blur,
            _ => null,
        };
        if (tool is { } t)
        {
            Editor.Tool = t;
            e.Handled = true;
        }
    }
}
