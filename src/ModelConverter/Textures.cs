using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GTWM.ModelConverter
{
    /// <summary>Gets any texture into a PNG or JPEG the game can decode, no bigger than needed.</summary>
    internal static class Textures
    {
        /// <summary>Returns encoded bytes (PNG when there's transparency, JPEG otherwise), or null if unreadable.</summary>
        public static byte[] Prepare(byte[] data, int maxSize, out string mime)
        {
            mime = null;
            Bitmap bmp = null;
            try
            {
                bool png = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
                bool jpg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;
                bmp = Decode(data);
                if (bmp == null)
                {
                    return null;
                }
                bool small = bmp.Width <= maxSize && bmp.Height <= maxSize;
                if (small && (png || jpg))
                {
                    mime = png ? "image/png" : "image/jpeg";
                    return data;
                }
                if (!small)
                {
                    float k = (float)maxSize / Math.Max(bmp.Width, bmp.Height);
                    Bitmap resized = Resize(bmp, Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k)));
                    bmp.Dispose();
                    bmp = resized;
                }
                bool alpha = HasAlpha(bmp);
                mime = alpha ? "image/png" : "image/jpeg";
                return Encode(bmp, alpha);
            }
            catch
            {
                return null;
            }
            finally
            {
                bmp?.Dispose();
            }
        }

        public static byte[] FromTexels(int width, int height, (byte r, byte g, byte b, byte a)[] texels)
        {
            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var pixels = new byte[width * height * 4];
                for (int i = 0; i < texels.Length && i < width * height; i++)
                {
                    pixels[i * 4] = texels[i].b;
                    pixels[i * 4 + 1] = texels[i].g;
                    pixels[i * 4 + 2] = texels[i].r;
                    pixels[i * 4 + 3] = texels[i].a;
                }
                Write(bmp, pixels);
                return Encode(bmp, png: true);
            }
        }

        private static Bitmap Decode(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                {
                    return new Bitmap(img);
                }
            }
            catch (ArgumentException)
            {
                // Not a GDI+ format; TGA is the usual suspect in game model packs.
                return DecodeTga(data);
            }
        }

        private static Bitmap DecodeTga(byte[] d)
        {
            if (d.Length < 18)
            {
                return null;
            }
            int idLength = d[0];
            int colorMapType = d[1];
            int type = d[2];
            int width = d[12] | (d[13] << 8);
            int height = d[14] | (d[15] << 8);
            int bpp = d[16];
            bool topDown = (d[17] & 0x20) != 0;
            if (colorMapType != 0 || (type != 2 && type != 10 && type != 3 && type != 11) || width <= 0 || height <= 0 || width > 16384 || height > 16384)
            {
                return null;
            }
            int bytes = bpp / 8;
            if (bytes != 1 && bytes != 3 && bytes != 4)
            {
                return null;
            }
            var src = new byte[width * height * bytes];
            int pos = 18 + idLength;
            if (type == 2 || type == 3)
            {
                Buffer.BlockCopy(d, pos, src, 0, Math.Min(src.Length, d.Length - pos));
            }
            else
            {
                int o = 0;
                while (o < src.Length && pos < d.Length)
                {
                    int header = d[pos++];
                    int count = (header & 0x7F) + 1;
                    if ((header & 0x80) != 0)
                    {
                        for (int i = 0; i < count && o < src.Length; i++)
                        {
                            Buffer.BlockCopy(d, pos, src, o, bytes);
                            o += bytes;
                        }
                        pos += bytes;
                    }
                    else
                    {
                        int n = Math.Min(count * bytes, Math.Min(src.Length - o, d.Length - pos));
                        Buffer.BlockCopy(d, pos, src, o, n);
                        o += n;
                        pos += count * bytes;
                    }
                }
            }
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int row = topDown ? y : height - 1 - y;
                for (int x = 0; x < width; x++)
                {
                    int s = (row * width + x) * bytes;
                    int t = (y * width + x) * 4;
                    if (bytes == 1)
                    {
                        pixels[t] = pixels[t + 1] = pixels[t + 2] = src[s];
                        pixels[t + 3] = 255;
                    }
                    else
                    {
                        pixels[t] = src[s];
                        pixels[t + 1] = src[s + 1];
                        pixels[t + 2] = src[s + 2];
                        pixels[t + 3] = bytes == 4 ? src[s + 3] : (byte)255;
                    }
                }
            }
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            Write(bmp, pixels);
            return bmp;
        }

        private static void Write(Bitmap bmp, byte[] bgra)
        {
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(bgra, y * bmp.Width * 4, bd.Scan0 + y * bd.Stride, bmp.Width * 4);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
        }

        private static bool HasAlpha(Bitmap bmp)
        {
            if (!Image.IsAlphaPixelFormat(bmp.PixelFormat))
            {
                return false;
            }
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                    for (int x = 3; x < row.Length; x += 4)
                    {
                        if (row[x] < 250)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
        }

        private static Bitmap Resize(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        private static byte[] Encode(Bitmap bmp, bool png)
        {
            using (var ms = new MemoryStream())
            {
                if (png)
                {
                    bmp.Save(ms, ImageFormat.Png);
                }
                else
                {
                    ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var p = new EncoderParameters(1);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                    bmp.Save(ms, jpeg, p);
                }
                return ms.ToArray();
            }
        }
    }
}
