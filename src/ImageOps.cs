using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace AnnotImg;

/// <summary>読み込んだ画像（Pbgra32 / 96dpi に正規化済み）。</summary>
public sealed class LoadedImage
{
    public required string Path { get; init; }
    public required BitmapSource Bitmap { get; init; }
    public required byte[] Pixels { get; init; }
    public int Width => Bitmap.PixelWidth;
    public int Height => Bitmap.PixelHeight;
    public bool HasAlpha { get; init; }
}

public static class ImageOps
{
    public static readonly string[] OpenExtensions = [".png", ".jpg", ".jpeg"];

    public static bool IsSupported(string path)
        => OpenExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    public static LoadedImage Load(string path)
    {
        BitmapSource frame;
        using (var fs = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        bool hasAlpha = frame.Format.Masks.Count == 4 || frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32
                        || frame.Format == PixelFormats.Rgba64 || frame.Format == PixelFormats.Prgba64 || frame.Format == PixelFormats.Rgba128Float
                        || (frame.Palette?.Colors.Any(c => c.A < 255) ?? false);

        frame = ApplyExifOrientation(frame);

        var conv = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        conv.CopyPixels(pixels, stride, 0);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return new LoadedImage { Path = path, Bitmap = bmp, Pixels = pixels, HasAlpha = hasAlpha };
    }

    /// <summary>JPEG の EXIF 回転情報を画素に反映する（スマホ写真が横倒しにならないように）。</summary>
    static BitmapSource ApplyExifOrientation(BitmapSource frame)
    {
        int orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var v = md.GetQuery("/app1/ifd/{ushort=274}");
                if (v != null) orientation = Convert.ToInt32(v);
            }
        }
        catch
        {
            // メタデータが読めない画像はそのまま扱う
        }

        var tg = new TransformGroup();
        switch (orientation)
        {
            case 2: tg.Children.Add(new ScaleTransform(-1, 1)); break;
            case 3: tg.Children.Add(new RotateTransform(180)); break;
            case 4: tg.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: tg.Children.Add(new RotateTransform(90)); tg.Children.Add(new ScaleTransform(-1, 1)); break;
            case 6: tg.Children.Add(new RotateTransform(90)); break;
            case 7: tg.Children.Add(new RotateTransform(270)); tg.Children.Add(new ScaleTransform(-1, 1)); break;
            case 8: tg.Children.Add(new RotateTransform(270)); break;
            default: return frame;
        }
        var tb = new TransformedBitmap(frame, tg);
        tb.Freeze();
        return tb;
    }

    /// <summary>
    /// 指定範囲をぼかした画像を返す。モザイク化してから平滑化するので、元の文字は復元・判読できない。
    /// </summary>
    public static BitmapSource BlurRegion(LoadedImage img, Int32Rect r)
    {
        int rw = r.Width, rh = r.Height;
        var buf = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
            Buffer.BlockCopy(img.Pixels, ((r.Y + y) * img.Width + r.X) * 4, buf, y * rw * 4, rw * 4);

        int block = Math.Clamp((int)Math.Round(Math.Min(rw, rh) / 4.0), 8, 48);
        Pixelate(buf, rw, rh, block);
        int radius = Math.Max(2, block / 2);
        BoxBlur(buf, rw, rh, radius);
        BoxBlur(buf, rw, rh, radius);

        var bmp = BitmapSource.Create(rw, rh, 96, 96, PixelFormats.Pbgra32, null, buf, rw * 4);
        bmp.Freeze();
        return bmp;
    }

    static void Pixelate(byte[] buf, int w, int h, int block)
    {
        for (int by = 0; by < h; by += block)
        {
            int ey = Math.Min(by + block, h);
            for (int bx = 0; bx < w; bx += block)
            {
                int ex = Math.Min(bx + block, w);
                long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                int count = (ey - by) * (ex - bx);
                for (int y = by; y < ey; y++)
                {
                    int i = (y * w + bx) * 4;
                    for (int x = bx; x < ex; x++, i += 4)
                    {
                        s0 += buf[i]; s1 += buf[i + 1]; s2 += buf[i + 2]; s3 += buf[i + 3];
                    }
                }
                byte c0 = (byte)(s0 / count), c1 = (byte)(s1 / count), c2 = (byte)(s2 / count), c3 = (byte)(s3 / count);
                for (int y = by; y < ey; y++)
                {
                    int i = (y * w + bx) * 4;
                    for (int x = bx; x < ex; x++, i += 4)
                    {
                        buf[i] = c0; buf[i + 1] = c1; buf[i + 2] = c2; buf[i + 3] = c3;
                    }
                }
            }
        }
    }

    static void BoxBlur(byte[] buf, int w, int h, int r)
    {
        var tmp = new byte[buf.Length];
        int win = 2 * r + 1;
        // 水平方向 buf -> tmp
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += buf[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x * 4 + c] = (byte)(sum / win);
                    int add = Math.Min(x + r + 1, w - 1), sub = Math.Max(x - r, 0);
                    sum += buf[row + add * 4 + c] - buf[row + sub * 4 + c];
                }
            }
        }
        // 垂直方向 tmp -> buf
        int stride = w * 4;
        for (int x = 0; x < w; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                int col = x * 4 + c;
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * stride + col];
                for (int y = 0; y < h; y++)
                {
                    buf[y * stride + col] = (byte)(sum / win);
                    int add = Math.Min(y + r + 1, h - 1), sub = Math.Max(y - r, 0);
                    sum += tmp[add * stride + col] - tmp[sub * stride + col];
                }
            }
        }
    }

    public static void Save(BitmapSource rendered, string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        using var ms = new MemoryStream();
        switch (ext)
        {
            case ".png":
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rendered));
                enc.Save(ms);
                break;
            }
            case ".jpg":
            case ".jpeg":
            {
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(rendered, PixelFormats.Bgr24, null, 0)));
                enc.Save(ms);
                break;
            }
            case ".webp":
                EncodeWebp(rendered, ms);
                break;
            default:
                throw new NotSupportedException($"未対応の保存形式です: {ext}");
        }
        // エンコードに成功してから書き込む（失敗時に既存ファイルを壊さない）
        File.WriteAllBytes(path, ms.ToArray());
    }

    static void EncodeWebp(BitmapSource src, Stream output)
    {
        var conv = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        conv.CopyPixels(pixels, stride, 0);

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90)
                         ?? throw new InvalidOperationException("WebP へのエンコードに失敗しました。");
        data.SaveTo(output);
    }
}
