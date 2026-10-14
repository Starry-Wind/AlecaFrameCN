using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace AlecaFrameZh
{
    public sealed class RewardLayout
    {
        public Rectangle[] Names;
        public int BarY;
    }

    public static class RelicLayout
    {
        private const double MinimumSelectionContrast = 80;
        // Reward selectors have separate outlined horizontal bars. A selected
        // bar becomes grey, so its colour must not determine the card count.
        // No resolution, aspect ratio or game UI scale is assumed here.
        public static RewardLayout FindRewards(Bitmap source)
        {
            using (Bitmap image = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
            {
                // DrawImageUnscaled can still honor JPEG DPI metadata. Explicit
                // pixel rectangles keep the captured geometry identical at 125%,
                // 150% or mixed monitor DPI.
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.DrawImage(source, new Rectangle(0, 0, image.Width, image.Height),
                        new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
                Rectangle bounds = new Rectangle(0, 0, image.Width, image.Height);
                BitmapData locked = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] pixels = new byte[locked.Stride * image.Height];
                int stride = locked.Stride;
                try { Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length); }
                finally { image.UnlockBits(locked); }

                int shortest = Math.Min(image.Width, image.Height);
                bool[] markers = SelectorPixels(pixels, stride, image.Width, image.Height, shortest);
                int minWidth = Math.Max(24, (int)(shortest * 0.045));
                int maxWidth = Math.Min((int)(image.Width * 0.85), (int)(image.Height * 0.6));
                // The white selected crest can cut a small hole through the
                // ridge mask at larger UI scales. Neighboring bars joined by
                // this tolerance are separated by their own crests below.
                int hole = Math.Max(2, shortest / 500);
                double bestScore = Double.MinValue;
                List<Rectangle> best = null;
                int bestY = 0;
                bool[] row = new bool[image.Width];
                for (int y = (int)(image.Height * 0.1); y < image.Height * 0.8; y++)
                {
                    for (int x = 0; x < image.Width; x++) row[x] = markers[y * image.Width + x];
                    List<Rectangle> runs = new List<Rectangle>();
                    int start = -1, last = -1;
                    for (int x = 0; x <= image.Width; x++)
                    {
                        if (x < image.Width && row[x])
                        {
                            if (start < 0) start = x;
                            last = x;
                        }
                        else if (start >= 0 && (x == image.Width || x - last > hole))
                        {
                            int width = last - start + 1;
                            // Resampling can join neighboring selector bars.
                            // Keep the entire ridge until its individual center
                            // crests have been checked below.
                            if (width >= minWidth && width <= image.Width * 0.85)
                                runs.Add(new Rectangle(start, y, width, 1));
                            start = -1;
                        }
                    }
                    runs = SeparateSelectors(pixels, stride, image.Width, image.Height, runs, minWidth, maxWidth);
                    for (int i = 0; i < runs.Count; i++)
                    {
                        List<Rectangle> group = new List<Rectangle> { runs[i] };
                        for (int j = i + 1; j < runs.Count; j++)
                        {
                            double width = group.Average(r => r.Width);
                            if (runs[j].Left - group.Last().Right > width * 0.12 ||
                                Math.Abs(runs[j].Width - width) > width * 0.2) break;
                            group.Add(runs[j]);
                        }
                        if (group.Count > 4) continue;
                        double average = group.Average(r => r.Width);
                        double center = (group[0].Left + group.Last().Right) / 2.0;
                        // Real one-to-four-card grids are centered on the game
                        // viewport. An off-center subset must never be accepted
                        // when a selected or faint bar is missing from detection.
                        if (Math.Abs(center - image.Width / 2.0) > average * 0.12) continue;
                        double contrast = 0;
                        bool valid = true;
                        foreach (Rectangle bar in group)
                        {
                            double edge = BarContrast(pixels, stride, image.Height, bar);
                            if (edge < 45) { valid = false; break; }
                            contrast += edge;
                        }
                        if (!valid) continue;
                        // Card borders also form a centered row of rectangles.
                        // Selectors have a diamond or white selection crest
                        // crossing their center; the card borders do not.
                        int marked = group.Count(bar => SelectionMark(pixels, stride, image.Width, image.Height, bar) > MinimumSelectionContrast);
                        if (marked < (group.Count > 2 ? group.Count - 1 : group.Count)) continue;
                        double score = group.Count * 1000 + average / shortest * 100 + contrast / group.Count / 10000;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = group;
                            bestY = y;
                        }
                    }
                }
                if (best == null)
                    throw new InvalidOperationException("未能定位遗物奖励卡片，请确保奖励选择画面完整且未被遮挡");
                Rectangle[] names = best.Select(bar =>
                    new Rectangle(bar.Left - 2, bestY - (int)Math.Ceiling(bar.Width * 0.34),
                        bar.Width + 4, (int)Math.Ceiling(bar.Width * 0.31))).ToArray();
                if (names.Any(area => !bounds.Contains(area) || area.Width < 20 || area.Height < 8))
                    throw new InvalidOperationException("奖励名称区域过小或被截图边界截断");
                return new RewardLayout { Names = names, BarY = bestY };
            }
        }

        private static List<Rectangle> SeparateSelectors(byte[] pixels, int stride, int width, int height,
            List<Rectangle> runs, int minWidth, int maxWidth)
        {
            List<Rectangle> result = new List<Rectangle>();
            foreach (Rectangle run in runs)
            {
                Rectangle[] split = null;
                for (int count = 4; count >= 2; count--)
                {
                    int pieceWidth = run.Width / count;
                    if (pieceWidth < minWidth || pieceWidth > maxWidth) continue;
                    Rectangle[] pieces = Enumerable.Range(0, count).Select(index =>
                    {
                        int left = run.Left + run.Width * index / count;
                        int right = run.Left + run.Width * (index + 1) / count;
                        return new Rectangle(left, run.Y, right - left, 1);
                    }).ToArray();
                    // A real selector has its own outlined ridge and center
                    // crest. Never infer additional cards from width alone.
                    if (pieces.All(piece => BarContrast(pixels, stride, height, piece) > 45 &&
                        SelectionMark(pixels, stride, width, height, piece) > MinimumSelectionContrast))
                    {
                        split = pieces;
                        break;
                    }
                }
                if (split != null) result.AddRange(split);
                else if (run.Width <= maxWidth) result.Add(run);
            }
            return result;
        }

        private static bool[] SelectorPixels(byte[] pixels, int stride, int width, int height, int shortest)
        {
            int[] light = new int[width * height];
            int[] above = new int[light.Length];
            bool[] result = new bool[light.Length];
            int[] queue = new int[height];
            int radius = Math.Max(3, (int)(shortest * 0.015));
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    light[y * width + x] = Luminance(pixels, stride, x, y);
            // Sliding minima find both dark outlines in linear time. This also
            // includes the white selection marker that crosses the grey bar.
            for (int x = 0; x < width; x++)
            {
                int head = 0, tail = 0;
                for (int y = 0; y < height; y++)
                {
                    while (head < tail && queue[head] < y - radius) head++;
                    int index = y * width + x;
                    above[index] = head < tail ? light[queue[head] * width + x] : Int32.MaxValue;
                    while (tail > head && light[queue[tail - 1] * width + x] >= light[index]) tail--;
                    queue[tail++] = y;
                }
                head = tail = 0;
                for (int y = height - 1; y >= 0; y--)
                {
                    while (head < tail && queue[head] > y + radius) head++;
                    int index = y * width + x, colorIndex = y * stride + x * 3;
                    int blue = pixels[colorIndex], green = pixels[colorIndex + 1], red = pixels[colorIndex + 2];
                    int minimum = Math.Min(red, Math.Min(green, blue));
                    int maximum = Math.Max(red, Math.Max(green, blue));
                    int below = head < tail ? light[queue[head] * width + x] : Int32.MaxValue;
                    bool neutralRidge = maximum - minimum < 60 && light[index] > 60 &&
                        light[index] - above[index] > 45 && light[index] - below > 45;
                    result[index] = IsWarm(pixels, stride, x, y) || neutralRidge;
                    while (tail > head && light[queue[tail - 1] * width + x] >= light[index]) tail--;
                    queue[tail++] = y;
                }
            }
            return result;
        }

        private static bool IsWarm(byte[] pixels, int stride, int x, int y)
        {
            int index = y * stride + x * 3;
            int blue = pixels[index], green = pixels[index + 1], red = pixels[index + 2];
            return red > 50 && red - green > 12 && red - blue > 25;
        }

        private static double BarContrast(byte[] pixels, int stride, int height, Rectangle bar)
        {
            int distance = Math.Max(2, (int)Math.Round(bar.Width * 0.04));
            if (bar.Y < distance || bar.Y + distance >= height) return 0;
            double middle = 0;
            // Avoid the diamond in the middle of each selector and its borders.
            foreach (double fraction in new[] { 0.1, 0.25, 0.4, 0.6, 0.75, 0.9 })
            {
                int x = bar.Left + (int)(bar.Width * fraction);
                middle += Luminance(pixels, stride, x, bar.Y);
            }
            double top = Double.MaxValue, bottom = Double.MaxValue;
            // Locate the dark outlines instead of sampling one fixed offset.
            // A resampled 720p bar can be only two pixels thick, and the scene
            // immediately outside its outline can be brighter than the bar.
            for (int offset = 1; offset <= distance; offset++)
            {
                double above = 0, below = 0;
                foreach (double fraction in new[] { 0.1, 0.25, 0.4, 0.6, 0.75, 0.9 })
                {
                    int x = bar.Left + (int)(bar.Width * fraction);
                    above += Luminance(pixels, stride, x, bar.Y - offset);
                    below += Luminance(pixels, stride, x, bar.Y + offset);
                }
                top = Math.Min(top, above);
                bottom = Math.Min(bottom, below);
            }
            return Math.Min(middle - top, middle - bottom) / 6;
        }

        private static double SelectionMark(byte[] pixels, int stride, int width, int height, Rectangle bar)
        {
            int center = bar.Left + bar.Width / 2;
            int distance = Math.Max(2, (int)Math.Round(bar.Width * 0.02));
            int side = Math.Max(4, (int)(bar.Width * 0.15));
            int search = Math.Max(2, (int)(bar.Width * 0.035));
            if (bar.Y < distance || bar.Y + distance >= height || center - side < 0 || center + side >= width) return 0;
            double best = Double.MinValue;
            for (int x = center - search; x <= center + search; x++)
            {
                double upper = Luminance(pixels, stride, x, bar.Y - distance) -
                    (Luminance(pixels, stride, center - side, bar.Y - distance) +
                     Luminance(pixels, stride, center + side, bar.Y - distance)) / 2.0;
                double lower = Luminance(pixels, stride, x, bar.Y + distance) -
                    (Luminance(pixels, stride, center - side, bar.Y + distance) +
                     Luminance(pixels, stride, center + side, bar.Y + distance)) / 2.0;
                best = Math.Max(best, Math.Min(upper, lower));
            }
            return best;
        }

        private static int Luminance(byte[] pixels, int stride, int x, int y)
        {
            int index = y * stride + x * 3;
            return 2 * pixels[index + 2] + pixels[index + 1] + pixels[index];
        }
    }
}
