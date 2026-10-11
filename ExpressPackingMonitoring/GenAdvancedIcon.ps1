$source = @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public class AdvancedIconGenerator {
    public static byte[] GenerateDib(Bitmap bmp) {
        int w = bmp.Width;
        int h = bmp.Height;
        int maskRowBytes = ((w + 31) / 32) * 4;
        int maskSize = maskRowBytes * h;
        int pixelSize = w * h * 4;
        byte[] dib = new byte[40 + pixelSize + maskSize];
        
        using (MemoryStream ms = new MemoryStream(dib))
        using (BinaryWriter bw = new BinaryWriter(ms)) {
            bw.Write((uint)40);        // biSize
            bw.Write((int)w);          // biWidth
            bw.Write((int)h * 2);      // biHeight (x2 for mask)
            bw.Write((ushort)1);       // biPlanes
            bw.Write((ushort)32);      // biBitCount
            bw.Write((uint)0);         // biCompression
            bw.Write((uint)pixelSize); // biSizeImage
            bw.Write((int)0);          // biXPelsPerMeter
            bw.Write((int)0);          // biYPelsPerMeter
            bw.Write((uint)0);         // biClrUsed
            bw.Write((uint)0);         // biClrImportant

            // BMPs are bottom-up, BGRA
            for (int y = h - 1; y >= 0; y--) {
                for (int x = 0; x < w; x++) {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write((byte)c.B);
                    bw.Write((byte)c.G);
                    bw.Write((byte)c.R);
                    bw.Write((byte)c.A);
                }
            }
            // The rest of the array is 0, which correctly defines the transparent AND mask for 32bpp
        }
        return dib;
    }

    /// <summary>
    /// 按尺寸画一张图标：白色圆角方块 + 蓝色"打包监控"（48 以下只画"包"）。
    /// app.ico 与网页头部用的 Web/logo.png 共用这一份画法，样式不会走偏。
    /// </summary>
    public static Bitmap CreateIconBitmap(int size) {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            int r = Math.Max(2, size / 5);
            int margin = Math.Max(1, size / 32);
            if (size <= 16) { margin = 0; r = 3; }

            using (GraphicsPath path = new GraphicsPath()) {
                int wPath = size - 2 * margin;
                int hPath = size - 2 * margin;
                path.AddArc(margin, margin, r*2, r*2, 180, 90);
                path.AddArc(margin+wPath-r*2, margin, r*2, r*2, 270, 90);
                path.AddArc(margin+wPath-r*2, margin+hPath-r*2, r*2, r*2, 0, 90);
                path.AddArc(margin, margin+hPath-r*2, r*2, r*2, 90, 90);
                path.CloseFigure();

                g.FillPath(Brushes.White, path);
                using (Pen pen = new Pen(Color.FromArgb(200, 210, 220), Math.Max(1, size/48))) {
                    g.DrawPath(pen, path);
                }
            }

            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(26, 115, 232)))
            using (StringFormat format = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                // 256, 64, 48 show "打包\n监控", 32 and 16 show "包"
                if (size >= 48) {
                    float fontSize = size * 0.25f + (size == 48 ? 1f : 0f);
                    float opticalOffsetY = size * 0.025f + (size == 48 ? 1f : 0f);
                    using (Font font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold)) {
                        RectangleF r1 = new RectangleF(0, margin + size * 0.05f + opticalOffsetY, size, size * 0.45f);
                        RectangleF r2 = new RectangleF(0, margin + size * 0.40f + opticalOffsetY, size, size * 0.45f);
                        g.DrawString("打包", font, textBrush, r1, format);
                        g.DrawString("监控", font, textBrush, r2, format);
                    }
                } else {
                    float fontSize = size <= 16 ? 11.5f : size * 0.60f;
                    using (Font font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold)) {
                        using (GraphicsPath textPath = new GraphicsPath()) {
                            textPath.AddString("包", font.FontFamily, (int)font.Style, font.Size, PointF.Empty, StringFormat.GenericTypographic);
                            RectangleF bounds = textPath.GetBounds();
                            using (Matrix transform = new Matrix()) {
                                transform.Translate(
                                    (size - bounds.Width) / 2f - bounds.X,
                                    (size - bounds.Height) / 2f - bounds.Y);
                                textPath.Transform(transform);
                            }
                            g.FillPath(textBrush, textPath);
                        }
                    }
                }
            }
        }
        return bmp;
    }

    /// <summary>网页头部用的图标：与 app.ico 同一份画法，只是单独存一张 PNG。</summary>
    public static void SaveWebLogo(string outputPath, int size) {
        using (Bitmap bmp = CreateIconBitmap(size)) {
            bmp.Save(outputPath, ImageFormat.Png);
        }
    }

    public static void BuildIcon(string outputPath) {
        int[] sizes = { 256, 64, 48, 32, 16 };
        
        using (FileStream fs = new FileStream(outputPath, FileMode.Create))
        using (BinaryWriter bw = new BinaryWriter(fs)) {
            // ICO Header
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)sizes.Length);

            byte[][] imageData = new byte[sizes.Length][];
            int offset = 6 + 16 * sizes.Length;

            for (int i = 0; i < sizes.Length; i++) {
                int size = sizes[i];
                using (Bitmap bmp = CreateIconBitmap(size)) {
                    // Windows compiler explicitly requires DIB format for <= 64 icons
                    if (size >= 256) {
                        using (MemoryStream ms = new MemoryStream()) {
                            bmp.Save(ms, ImageFormat.Png);
                            imageData[i] = ms.ToArray();
                        }
                    } else {
                        imageData[i] = GenerateDib(bmp);
                    }
                }

                // Directory Entry
                byte bW = size >= 256 ? (byte)0 : (byte)size;
                bw.Write(bW);
                bw.Write(bW);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(imageData[i].Length);
                bw.Write(offset);
                offset += imageData[i].Length;
            }

            for (int i = 0; i < sizes.Length; i++) {
                bw.Write(imageData[i]);
            }
        }
    }
}
"@

$drawingReferences = if ($PSVersionTable.PSEdition -eq 'Core') {
    @(
        'System.Drawing.Common'
        'System.Drawing.Primitives'
        'System.Private.Windows.GdiPlus'
        'System.Private.Windows.Core'
    ) | ForEach-Object { [System.Reflection.Assembly]::Load($_).Location }
} else {
    @('System.Drawing')
}

Add-Type -TypeDefinition $source -ReferencedAssemblies $drawingReferences -ErrorAction Stop
$outputPath = Join-Path $PSScriptRoot "app.ico"
[AdvancedIconGenerator]::BuildIcon($outputPath)
Write-Host "Multi-resolution icon generated successfully."

# 网页头部（Web/index.html）直接引用这张图，尺寸按显示大小出图，浏览器不用缩放。
$webLogoPath = Join-Path $PSScriptRoot "Web\logo.png"
[AdvancedIconGenerator]::SaveWebLogo($webLogoPath, 96)
Write-Host "Web logo generated: $webLogoPath"
