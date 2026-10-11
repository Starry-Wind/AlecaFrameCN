using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AlecaFrameZh
{
    public static class RelicHelper
    {
        private static readonly object MapLock = new object();
        private static Dictionary<string, string> names;

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

        public static string ResolveEvidence(IEnumerable<string> observations)
        {
            List<string> keys = observations.Where(value => !String.IsNullOrWhiteSpace(value))
                .Select(NormalizeOcr).Where(value => value.Length >= 5).ToList();
            if (keys.Count < 2) return null;
            Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> closest = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                Dictionary<string, int> current = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> candidate in GetNames())
                {
                    int score = Distance(key, candidate.Key);
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
                ranked[1].Value - ranked[0].Value < 2 || closest[winner] > 3)
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

        private static List<string> RunOcr(string dir, bool sortByX)
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
                (sortByX ? " -SortByX" : ""));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(25000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("中文遗物识别超时");
                }
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
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
            if (key.EndsWith("prime监", StringComparison.Ordinal))
                key = key.Substring(0, key.Length - 1) + "蓝图";
            else if (key.EndsWith("prime蓝", StringComparison.Ordinal))
                key += "图";
            string exact;
            if (map.TryGetValue(key, out exact)) return exact;
            string bestName = null;
            int best = Int32.MaxValue, second = Int32.MaxValue;
            Dictionary<string, int> scores = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> candidate in map)
            {
                int score = Distance(key, candidate.Key);
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

        private static string NormalizeOcr(string value)
        {
            return Normalize(value).Replace("礻几体", "机体")
                .Replace("枳体", "机体")
                .Replace("枧体", "机体")
                .Replace("几体", "机体");
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
