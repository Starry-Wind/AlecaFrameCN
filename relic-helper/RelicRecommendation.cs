using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;

namespace AlecaFrameZh
{
    // Loaded with the existing native Chinese patch. Only the recommendation
    // handler calls these methods; other OCR and language checks stay official.
    public static class RelicRecommendation
    {
        public static int GetSupportedLanguage(int originalLanguage, int englishLanguage)
        {
            if (!TradeHelper.IsChineseGame()) return originalLanguage;
            Log("已启用中文遗物推荐");
            return englishLanguage;
        }

        public static string ReadCategory(Bitmap image, Func<Bitmap, string> officialOcr)
        {
            if (!TradeHelper.IsChineseGame()) return officialOcr(image);
            try
            {
                SaveCrop(image);
                string raw = RelicHelper.RecognizeRaw(image);
                string originalRaw = raw;
                string category = ResolveCategory(raw);
                if (category == null)
                {
                    // Short headings can be missed on the game's dark panel.
                    // Retry enlarged dark text on a light background.
                    using (var enlarged = new Bitmap(image.Width * 2, image.Height * 2))
                    using (var graphics = Graphics.FromImage(enlarged))
                    using (var attributes = new ImageAttributes())
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        attributes.SetColorMatrix(new ColorMatrix(new float[][] {
                            new float[] { -1, 0, 0, 0, 0 }, new float[] { 0, -1, 0, 0, 0 },
                            new float[] { 0, 0, -1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 },
                            new float[] { 1, 1, 1, 0, 1 }
                        }));
                        graphics.DrawImage(image, new Rectangle(0, 0, enlarged.Width, enlarged.Height),
                            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                        raw = RelicHelper.RecognizeRaw(enlarged);
                        category = ResolveCategory(raw);
                    }
                }
                Log("纪元识别：原图=" + Flatten(originalRaw) + "; 处理后=" + Flatten(raw) + " -> " + (category ?? "未识别"));
                return category ?? "";
            }
            catch (Exception ex)
            {
                Log("中文纪元识别失败：" + ex.Message);
                // The official handler retries and displays its normal error.
                return "";
            }
        }

        public static string ResolveCategory(string text)
        {
            string normalized = Regex.Replace(text ?? "", @"\s+", "").ToLowerInvariant().Replace('紀', '纪').Replace('後', '后');
            // Game headings use 前世纪/中世纪, while relic item names and the
            // bilingual dictionary use 前纪/中纪. Accept both forms for all eras.
            normalized = Regex.Replace(normalized, @"([古前中后])世纪", "$1纪");
            string[] chinese = { "古纪", "前纪", "中纪", "后纪", "安魂" };
            string[] english = { "lith", "meso", "neo", "axi", "requiem" };
            for (int i = 0; i < chinese.Length; i++)
                if (normalized.Contains(chinese[i]) || normalized.Contains(english[i])) return english[i];
            if (normalized.Contains("全部") || normalized.Contains("所有") || normalized.Contains("all")) return "all";
            return null;
        }

        private static string StateRoot()
        {
            return Path.Combine(Environment.GetEnvironmentVariable("ALECAFRAME_ZH_TEST_LOCAL_ROOT") ??
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlecaFrame-ZH-Patch");
        }

        private static string Flatten(string text)
        {
            return (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }

        private static void SaveCrop(Bitmap image)
        {
            try
            {
                string root = StateRoot();
                Directory.CreateDirectory(root);
                // Keep only the latest category crop, for real-image replay.
                image.Save(Path.Combine(root, "relic-recommendation-crop.png"), ImageFormat.Png);
            }
            catch (Exception ex) { Log("保存纪元截图失败：" + ex.Message); }
        }

        private static void Log(string message)
        {
            try
            {
                string root = StateRoot();
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "relic-recommendation.log"), DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
