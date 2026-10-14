using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AlecaFrameZh
{
    public sealed class OcrTextLine
    {
        public string Text;
        public double X, Y, Width, Height;
    }

    public static class RelicHelper
    {
        private static readonly object MapLock = new object();
        private static Dictionary<string, string> names;
        private static readonly HashSet<string> componentAliases = new HashSet<string>(StringComparer.Ordinal);

        public static string GetDisplayName(string english, string fallback)
        {
            return ItemNames.Chinese(english, fallback);
        }

        public static bool IsChineseGame()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "AlecaFrame", "lastLang.tmp");
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        public static List<string> Recognize(List<Bitmap> bitmaps)
        {
            if (bitmaps == null || bitmaps.Count == 0 || bitmaps.Count % 2 != 0)
                throw new InvalidOperationException("中文遗物截图区域不完整");
            string dir = Path.Combine(Path.GetTempPath(), "AlecaFrameZhOcr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                for (int i = 0; i < bitmaps.Count; i += 2)
                {
                    int width = Math.Max(bitmaps[i].Width, bitmaps[i + 1].Width);
                    int height = bitmaps[i].Height + bitmaps[i + 1].Height;
                    using (Bitmap combined = new Bitmap(width * 3, height * 3))
                    using (Graphics graphics = Graphics.FromImage(combined))
                    {
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.DrawImage(bitmaps[i], new Rectangle(0, 0, bitmaps[i].Width * 3,
                            bitmaps[i].Height * 3));
                        graphics.DrawImage(bitmaps[i + 1], new Rectangle(0, bitmaps[i].Height * 3,
                            bitmaps[i + 1].Width * 3, bitmaps[i + 1].Height * 3));
                        combined.Save(Path.Combine(dir, (i / 2).ToString("D2") + ".png"), ImageFormat.Png);
                    }
                }

                List<string> texts = RunOcr(dir, false);
                if (texts.Count != bitmaps.Count / 2)
                    throw new InvalidOperationException("中文遗物文字识别结果数量不正确");
                Dictionary<string, string> map = GetNames();
                List<string> results = new List<string>();
                foreach (string text in texts)
                {
                    string english = Resolve(text, map);
                    if (english == null)
                        throw new InvalidOperationException("无法可靠识别中文遗物奖励：" + text);
                    results.Add(english.ToLowerInvariant());
                }
                return results;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        public static string RecognizeRaw(Bitmap source)
        {
            return RecognizeRawBatch(new List<Bitmap> { source }).FirstOrDefault() ?? "";
        }

        public static string RecognizeRawSorted(Bitmap source)
        {
            return RecognizeRawBatch(new List<Bitmap> { source }, true).FirstOrDefault() ?? "";
        }

        public static List<string> RecognizeRawBatch(List<Bitmap> images, bool sortByX = false)
        {
            if (images == null || images.Count == 0)
                throw new InvalidOperationException("没有可识别的奖励文字区域");
            string dir = Path.Combine(Path.GetTempPath(), "AlecaFrameZhOcr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                for (int i = 0; i < images.Count; i++)
                    images[i].Save(Path.Combine(dir, i.ToString("D2") + ".png"), ImageFormat.Png);
                List<string> result = RunOcr(dir, sortByX);
                if (result.Count != images.Count)
                    throw new InvalidOperationException("奖励文字区域识别结果数量不正确");
                return result;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        public static string ResolveOne(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return null;
            string english = Resolve(raw, GetNames());
            return english == null ? null : english.ToLowerInvariant();
        }

        public static List<OcrTextLine> RecognizeTextLines(Bitmap image)
        {
            string dir = Path.Combine(Path.GetTempPath(), "AlecaFrameZhOcr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                int height = Math.Max(1, Math.Min(image.Height, (int)(1600.0 * image.Height / image.Width)));
                using (Bitmap normalized = ResizeRegion(image, new Rectangle(0, 0, image.Width, image.Height), height))
                    normalized.Save(Path.Combine(dir, "00.png"), ImageFormat.Png);
                string json = RunOcr(dir, false, true).Single();
                return JArray.Parse(json).Select(line => new OcrTextLine {
                    Text = (string)line["text"], X = (double)line["x"], Y = (double)line["y"],
                    Width = (double)line["width"], Height = (double)line["height"]
                }).ToList();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        public static List<string> RecognizeRewardCards(Bitmap image, RewardLayout layout)
        {
            int[] quantities;
            return RecognizeRewardCards(image, layout, out quantities);
        }

        public static List<string> RecognizeRewardCards(Bitmap image, RewardLayout layout, out int[] quantities)
        {
            quantities = new int[layout.Names.Length];
            List<Bitmap> variants = new List<Bitmap>();
            try
            {
                foreach (Rectangle area in layout.Names)
                {
                    using (Bitmap enlarged = ResizeRegion(image, area, 110))
                    {
                        variants.Add((Bitmap)enlarged.Clone());
                        variants.Add(TextMask(enlarged, 90, 65, 18, 7));
                        variants.Add(TextMask(enlarged, 140, 105, 27, 13));
                        variants.Add(Invert(enlarged));
                    }
                }
                List<string> observations = RecognizeRawBatch(variants);
                List<string> names = new List<string>();
                for (int i = 0; i < layout.Names.Length; i++)
                {
                    string[] readings = observations.Skip(i * 4).Take(4).ToArray();
                    string winner = ResolveRewardReadings(readings);
                    if (winner == null)
                    {
                        // A selected card can wash out its gold letters. Retry
                        // that same card at two raster sizes, never fewer cards.
                        List<Bitmap> retry = new List<Bitmap>();
                        try
                        {
                            foreach (int height in new[] { 75, 160 })
                                using (Bitmap enlarged = ResizeRegion(image, layout.Names[i], height))
                                {
                                    retry.Add((Bitmap)enlarged.Clone());
                                    retry.Add(TextMask(enlarged, 135, 95, 23, 10));
                                }
                            readings = readings.Concat(RecognizeRawBatch(retry)).ToArray();
                            winner = ResolveRewardReadings(readings);
                        }
                        finally { foreach (Bitmap bitmap in retry) bitmap.Dispose(); }
                    }
                    if (Environment.GetEnvironmentVariable("ALECAFRAME_ZH_OCR_DEBUG") == "1")
                        Console.Error.WriteLine("card=" + i + " area=" + layout.Names[i] + " OCR=" +
                            String.Join(" || ", readings.Select(value => value.Replace("\n", " "))) + " result=" + winner);
                    if (winner == null)
                        throw new InvalidOperationException("检测到 " + layout.Names.Length + " 张奖励卡片，但第 " +
                            (i + 1) + " 件文字未能可靠识别；不会隐藏其他奖励或减少奖励数量");
                    names.Add(winner);
                    var amounts = readings.Select(value => Regex.Match(value, @"^\s*(?<count>[1-9][0-9]{0,2})\s*[xX×]\s*"))
                        .Where(match => match.Success).Select(match => Int32.Parse(match.Groups["count"].Value))
                        .GroupBy(amount => amount).OrderByDescending(group => group.Count()).ToList();
                    quantities[i] = amounts.Count > 0 && amounts[0].Count() >= 2 ? amounts[0].Key : 1;
                }
                return names;
            }
            finally { foreach (Bitmap variant in variants) variant.Dispose(); }
        }

        private static string ResolveRewardReadings(IEnumerable<string> readings)
        {
            var resolved = readings.Select(ResolveOne).Where(name => name != null)
                .GroupBy(name => name).OrderByDescending(group => group.Count()).ToList();
            if (resolved.Count > 0 && resolved[0].Count() >= 2 &&
                (resolved.Count == 1 || resolved[0].Count() > resolved[1].Count())) return resolved[0].Key;
            return ResolveEvidence(readings);
        }

        private static Bitmap ResizeRegion(Bitmap image, Rectangle area, int targetHeight)
        {
            int width = Math.Max(1, (int)Math.Round(area.Width * targetHeight / (double)area.Height));
            Bitmap result = new Bitmap(width, targetHeight);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(image, new Rectangle(0, 0, width, targetHeight), area, GraphicsUnit.Pixel);
            }
            return result;
        }

        private static Bitmap TextMask(Bitmap image, int minRed, int minGreen, int redBlue, int greenBlue)
        {
            Bitmap result = new Bitmap(image.Width, image.Height);
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    Color color = image.GetPixel(x, y);
                    result.SetPixel(x, y, color.R > minRed && color.G > minGreen &&
                        color.R - color.B > redBlue && color.G - color.B > greenBlue ? Color.Black : Color.White);
                }
            return result;
        }

        private static Bitmap Invert(Bitmap image)
        {
            Bitmap result = new Bitmap(image.Width, image.Height);
            using (Graphics graphics = Graphics.FromImage(result))
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[] { -1, 0, 0, 0, 0 }, new float[] { 0, -1, 0, 0, 0 },
                    new float[] { 0, 0, -1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 1, 1, 1, 0, 1 }
                }));
                graphics.DrawImage(image, new Rectangle(0, 0, result.Width, result.Height),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
            return result;
        }

        public static string ResolveEvidence(IEnumerable<string> observations)
        {
            List<string> keys = observations.Where(value => !String.IsNullOrWhiteSpace(value))
                .Select(NormalizeOcr).Where(value => value.Length >= 5 &&
                    !value.StartsWith("prime", StringComparison.Ordinal)).ToList();
            // Strong masks can erase a wrapped second line at low resolution.
            // Compare the complete readings; a much shorter fragment is not an
            // independent vote for a different Prime component.
            if (keys.Count > 0)
            {
                int longest = keys.Max(value => value.Length);
                keys = keys.Where(value => value.Length >= longest * 0.7).ToList();
            }
            if (keys.Count < 2) return null;
            Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> closest = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                bool hasPartText = HasPartText(key);
                Dictionary<string, int> current = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> candidate in GetNames())
                {
                    if (!hasPartText && componentAliases.Contains(candidate.Key)) continue;
                    int score = MatchDistance(key, candidate.Key);
                    int previous;
                    if (!current.TryGetValue(candidate.Value, out previous) || score < previous)
                        current[candidate.Value] = score;
                }
                foreach (KeyValuePair<string, int> candidate in current)
                {
                    if (!totals.ContainsKey(candidate.Key))
                    {
                        totals[candidate.Key] = 0;
                        closest[candidate.Key] = Int32.MaxValue;
                    }
                    totals[candidate.Key] += candidate.Value;
                    closest[candidate.Key] = Math.Min(closest[candidate.Key], candidate.Value);
                }
            }
            var ranked = totals.OrderBy(candidate => candidate.Value).Take(2).ToList();
            if (ranked.Count < 2) return null;
            string winner = ranked[0].Key;
            if (ranked[0].Value > keys.Count * 5 ||
                ranked[1].Value - ranked[0].Value < 2 || closest[winner] > 4)
                return null;
            return winner.ToLowerInvariant();
        }

        public static void Warmup()
        {
            GetNames();
        }

        public static List<string> ResolveLines(string raw)
        {
            Dictionary<string, string> map = GetNames();
            List<string> result = new List<string>();
            foreach (string line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Count(Char.IsLetterOrDigit) < 5)
                    continue;
                string english = Resolve(line, map);
                if (english == null)
                    throw new InvalidOperationException("无法可靠识别中文遗物奖励：" + line);
                result.Add(english.ToLowerInvariant());
            }
            if (result.Count < 1 || result.Count > 4)
                throw new InvalidOperationException("中文遗物奖励数量不正确");
            return result;
        }

        private static List<string> RunOcr(string dir, bool sortByX, bool lineBounds = false)
        {
            string script = Path.Combine(Path.GetDirectoryName(typeof(RelicHelper).Assembly.Location), "ZhRelicOcr.ps1");
            if (!File.Exists(script))
                throw new FileNotFoundException("缺少中文遗物 OCR 脚本", script);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string shell = Path.Combine(windows,
                Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32",
                "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo info = new ProcessStartInfo(shell,
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -ImageDirectory \"" + dir + "\"" +
                (sortByX ? " -SortByX" : "") + (lineBounds ? " -LineBounds" : ""));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process process = Process.Start(info))
            {
                // Bound information can exceed a pipe's buffer. Drain both
                // streams while OCR runs rather than waiting before reading.
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(25000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("中文遗物识别超时");
                }
                string output = outputTask.Result;
                string error = errorTask.Result;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("中文遗物识别失败：" + error.Trim());
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim() == "-" ? "" :
                        Encoding.UTF8.GetString(Convert.FromBase64String(line.Trim())))
                    .ToList();
            }
        }

        private static Dictionary<string, string> GetNames()
        {
            if (names != null) return names;
            lock (MapLock)
            {
                if (names != null) return names;
                string root = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "AlecaFrame", "cachedData", "json");
                JObject lang = JObject.Parse(File.ReadAllText(Path.Combine(root, "lang.json"), Encoding.UTF8));
                JArray components = JArray.Parse(File.ReadAllText(Path.Combine(root, "Components.json"), Encoding.UTF8));
                Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JToken part in components)
                {
                    string english = (string)part.SelectToken("drops[0].type");
                    if (String.IsNullOrEmpty(english) ||
                        (!english.Contains("Prime") && !english.Contains("Forma Blueprint")))
                        continue;
                    string parentKey = (string)part.SelectToken("parentUniqueNames[0]");
                    string partKey = (string)part["uniqueName"];
                    string parent = GetChinese(lang, parentKey);
                    string suffix = GetChinese(lang, partKey);
                    if (String.IsNullOrWhiteSpace(suffix) && (string)part["name"] == "Blueprint")
                        suffix = "蓝图";
                    if (String.IsNullOrWhiteSpace(parent) || String.IsNullOrWhiteSpace(suffix))
                        continue;
                    string chinese = Normalize(parent + suffix);
                    if (!result.ContainsKey(chinese)) result.Add(chinese, english);
                    string preferred = Normalize(GetDisplayName(english, parent + suffix));
                    if (!result.ContainsKey(preferred)) result.Add(preferred, english);
                    // At low resolution the Chinese OCR engine can omit the
                    // small Latin "Prime" between an English Warframe name
                    // and its Chinese component. Keep both the base name and
                    // component in the alias; a bare Warframe is insufficient.
                    bool warframePart = english.EndsWith(" Chassis Blueprint", StringComparison.Ordinal) ||
                        english.EndsWith(" Systems Blueprint", StringComparison.Ordinal) ||
                        english.EndsWith(" Neuroptics Blueprint", StringComparison.Ordinal);
                    if (warframePart && Regex.IsMatch(preferred, @"^[a-z0-9]+prime") && HasPartText(preferred))
                    {
                        string mixedScriptAlias = preferred.Replace("prime", "");
                        if (!result.ContainsKey(mixedScriptAlias))
                        {
                            result.Add(mixedScriptAlias, english);
                            componentAliases.Add(mixedScriptAlias);
                        }
                    }
                    if (english.EndsWith(" Blueprint", StringComparison.OrdinalIgnoreCase) &&
                        suffix != "蓝图")
                    {
                        string withBlueprint = Normalize(parent + suffix + "蓝图");
                        if (!result.ContainsKey(withBlueprint)) result.Add(withBlueprint, english);
                    }
                }
                names = result;
                return names;
            }
        }

        private static string GetChinese(JObject lang, string key)
        {
            if (key == null) return null;
            JToken entry = lang[key];
            return entry == null ? null : (string)entry.SelectToken("zh.name");
        }

        private static string Resolve(string ocr, Dictionary<string, string> map)
        {
            string key = NormalizeOcr(ocr);
            if (key.StartsWith("prime", StringComparison.Ordinal)) return null;
            if (key.EndsWith("prime监", StringComparison.Ordinal))
                key = key.Substring(0, key.Length - 1) + "蓝图";
            else if (key.EndsWith("prime蓝", StringComparison.Ordinal))
                key += "图";
            string exact;
            if (map.TryGetValue(key, out exact)) return exact;
            string bestName = null;
            int best = Int32.MaxValue, second = Int32.MaxValue;
            Dictionary<string, int> scores = new Dictionary<string, int>(StringComparer.Ordinal);
            bool hasPartText = HasPartText(key);
            foreach (KeyValuePair<string, string> candidate in map)
            {
                if (!hasPartText && componentAliases.Contains(candidate.Key)) continue;
                int score = MatchDistance(key, candidate.Key);
                int previous;
                if (!scores.TryGetValue(candidate.Value, out previous) || score < previous)
                    scores[candidate.Value] = score;
            }
            foreach (KeyValuePair<string, int> candidate in scores)
            {
                int score = candidate.Value;
                if (score < best) { second = best; best = score; bestName = candidate.Key; }
                else if (score < second) second = score;
            }
            int allowance = key.Length >= 10 ? 3 : 1;
            return best <= allowance && second - best >= 1 ? bestName : null;
        }

        private static string Normalize(string value)
        {
            return new string(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
                .Where(Char.IsLetterOrDigit).ToArray());
        }

        private static bool HasPartText(string value)
        {
            return value.Replace("蓝图", "").Any(character => character >= '\u3400' && character <= '\u9fff');
        }

        private static string NormalizeOcr(string value)
        {
            // Reward bundles display e.g. "2 X Forma 蓝图". The multiplier is
            // separate from the item's canonical English identity and ID.
            value = Regex.Replace(value, @"^\s*[1-9][0-9]{0,2}\s*[xX×]\s*", "");
            return Normalize(value).Replace("礻几体", "机体")
                .Replace("枳体", "机体")
                .Replace("枧体", "机体")
                .Replace("几体", "机体");
        }

        private static int MatchDistance(string observed, string candidate)
        {
            int score = Distance(observed, candidate);
            int sourcePrime = observed.IndexOf("prime", StringComparison.Ordinal);
            int targetPrime = candidate.IndexOf("prime", StringComparison.Ordinal);
            if (sourcePrime > 0 && targetPrime > 0)
            {
                string sourceParent = observed.Substring(0, sourcePrime);
                string targetParent = candidate.Substring(0, targetPrime);
                char[] parentLetters = sourceParent.Where(character => character >= '\u3400' && character <= '\u9fff').ToArray();
                // A missing parent glyph and a contradictory parent glyph can
                // have the same edit distance. Shared Prime/component text
                // must not outweigh the parent characters actually observed.
                if (parentLetters.Length > 0 && HasPartText(targetParent) &&
                    !parentLetters.Any(character => targetParent.IndexOf(character) >= 0)) score++;
            }
            return score;
        }

        private static int Distance(string a, string b)
        {
            int[] row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int left = i, corner = i - 1;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, left + 1), corner + (a[i - 1] == b[j - 1] ? 0 : 1));
                    corner = above;
                    left = row[j];
                }
                row[0] = i;
            }
            return row[b.Length];
        }
    }
}
