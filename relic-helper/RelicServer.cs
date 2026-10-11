using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AlecaFrameZh;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class RelicServer
{
    private const int Port = 37619;
    private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "AlecaFrame", "cachedData", "json");
    private static readonly string RelicLogRoot = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "AlecaFrame", "relicLogs");
    private static readonly string ResultRoot = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "AlecaFrame-ZH-Patch");
    private static readonly string ResultFile = Path.Combine(ResultRoot, "relic-result.json");
    private static Dictionary<string, JObject> parts;
    private static Dictionary<string, JObject> parents;
    private static JObject languages;

    private sealed class Reward
    {
        public string englishName;
        public string name;
        public string internalName;
        public string parentName;
        public string parentInternalName;
        public int ducats;
        public int totalToOwn;
        public bool isItemVaulted;
    }

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--test")
        {
            try
            {
                Console.WriteLine(JsonConvert.SerializeObject(new { ok = true, rewards = ReadLog(args[1]) }));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--test-warm")
        {
            EnsureData();
            RelicHelper.Warmup();
            DateTime started = DateTime.UtcNow;
            List<Reward> rewards = ReadLog(args[1]);
            Console.WriteLine(JsonConvert.SerializeObject(new {
                ok = true, elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds, rewards
            }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--publish-test")
        {
            PublishResult(args[1]);
            Console.WriteLine(ResultFile);
            return 0;
        }
        if (args.Length != 1 || args[0] != "--serve") return 2;
        EnsureData();
        RelicHelper.Warmup();
        Thread watcher = new Thread(WatchRelicLogs);
        watcher.IsBackground = true;
        watcher.Start();
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        while (true)
        {
            using (TcpClient client = listener.AcceptTcpClient())
            {
                try { Handle(client); }
                catch { }
            }
        }
    }

    private static void WatchRelicLogs()
    {
        string processed = null;
        while (true)
        {
            try
            {
                string latest = Directory.GetFiles(RelicLogRoot, "*.alecalog")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (latest != null && latest != processed &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) > TimeSpan.FromMilliseconds(400) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) < TimeSpan.FromSeconds(60))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(latest);
                    long sourceMtime = new DateTimeOffset(modified).ToUnixTimeMilliseconds();
                    try
                    {
                        int rewardCount = PublishResult(latest);
                        AppendLog("recognized " + Path.GetFileName(latest) + " rewards=" + rewardCount);
                        processed = latest;
                    }
                    catch (IOException) { }
                    catch (InvalidDataException) { }
                    catch (Exception ex)
                    {
                        WriteResult(new { ok = false, sourceMtime, error = ex.Message });
                        AppendLog("failed " + Path.GetFileName(latest) + ": " + ex.Message);
                        processed = latest;
                    }
                }
            }
            catch (Exception ex) { AppendLog("watcher: " + ex.Message); }
            Thread.Sleep(250);
        }
    }

    private static int PublishResult(string path)
    {
        long sourceMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        List<Reward> rewards = ReadLog(path);
        WriteResult(new { ok = true, sourceMtime, rewards });
        return rewards.Count;
    }

    private static void WriteResult(object result)
    {
        Directory.CreateDirectory(ResultRoot);
        string temp = ResultFile + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(result), new UTF8Encoding(false));
        if (File.Exists(ResultFile)) File.Replace(temp, ResultFile, null);
        else File.Move(temp, ResultFile);
    }

    private static void AppendLog(string message)
    {
        try
        {
            Directory.CreateDirectory(ResultRoot);
            File.AppendAllText(Path.Combine(ResultRoot, "relic-service.log"),
                DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static void Handle(TcpClient client)
    {
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        NetworkStream stream = client.GetStream();
        StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
        string line = reader.ReadLine();
        if (line == null) return;
        string[] first = line.Split(' ');
        while (!String.IsNullOrEmpty(reader.ReadLine())) { }
        string body;
        int status = 200;
        try
        {
            if (first.Length < 2) throw new InvalidOperationException("Bad request");
            if (first[0] == "OPTIONS")
                body = "{}";
            else if (first[1] == "/health")
                body = JsonConvert.SerializeObject(new { ok = true, service = "AlecaFrameZhRelicServer" });
            else if (first[1].StartsWith("/relic", StringComparison.Ordinal))
                body = JsonConvert.SerializeObject(new { ok = true, rewards = ReadLatestLog() });
            else { status = 404; body = "{\"ok\":false,\"error\":\"Not found\"}"; }
        }
        catch (Exception ex)
        {
            status = 500;
            body = JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
        }
        byte[] data = Encoding.UTF8.GetBytes(body);
        string header = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            "Content-Length: " + data.Length + "\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    private static List<Reward> ReadLatestLog()
    {
        string root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "AlecaFrame", "relicLogs");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string latest = Directory.GetFiles(root, "*.alecalog")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) < TimeSpan.FromSeconds(35))
            {
                try { return ReadLog(latest); }
                catch (IOException) { }
            }
            Thread.Sleep(300);
        }
        throw new InvalidOperationException("没有找到刚刚的中文遗物截图");
    }

    private static List<Reward> ReadLog(string path)
    {
        using (ZipArchive zip = ZipFile.OpenRead(path))
        {
            ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("original.jpeg", StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new InvalidOperationException("遗物记录没有原始截图");
            using (Stream imageStream = entry.Open())
            using (Bitmap original = new Bitmap(imageStream))
            {
                try
                {
                    List<string> goldNames = RecognizeGold(original);
                    EnsureData();
                    return goldNames.Select(MakeReward).ToList();
                }
                catch (InvalidOperationException ex) { Debug("gold recognition: " + ex.Message); }
                int estimated = 4;
                try
                {
                    List<string> fastNames = RecognizeStrip(original, out estimated);
                    if (fastNames.Count != estimated)
                        throw new InvalidOperationException("整幅截图识别数量与奖励卡片数量不一致");
                    EnsureData();
                    return fastNames.Select(MakeReward).ToList();
                }
                catch (InvalidOperationException ex) { Debug("strip recognition: " + ex.Message); }
                if (Environment.GetEnvironmentVariable("ALECAFRAME_ZH_OCR_DEBUG") == "1")
                    Console.Error.WriteLine("estimated count=" + estimated);
                int[] counts = new[] { estimated, 4, 3, 2, 1 }.Distinct().ToArray();
                foreach (int count in counts)
                {
                    foreach (bool legacy in new[] { false, true })
                    {
                        try
                        {
                            List<string> names = Recognize(original, count, legacy);
                            EnsureData();
                            return names.Select(MakeReward).ToList();
                        }
                        catch (InvalidOperationException ex) {
                            Debug("count=" + count + " legacy=" + legacy + ": " + ex.Message);
                        }
                    }
                }
            }
        }
        throw new InvalidOperationException("无法可靠识别这次中文遗物奖励");
    }

    private static List<string> RecognizeGold(Bitmap image)
    {
        int y = (int)(image.Height * 0.3785f);
        int height = (int)(image.Height * 0.0528f);
        Rectangle stripArea = new Rectangle(image.Width / 4, y, image.Width / 2, height);
        string raw;
        using (Bitmap strip = GoldText(image, stripArea, 1))
            raw = RelicHelper.RecognizeRawSorted(strip);
        Debug("gold strip OCR=" + raw.Replace("\r", " ").Replace("\n", " | "));
        List<string> lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Count(Char.IsLetterOrDigit) >= 5).ToList();
        if (lines.Count >= 1 && lines.Count <= 4)
        {
            List<string> quick = lines.Select(RelicHelper.ResolveOne).ToList();
            if (quick.All(name => name != null)) return quick;
        }
        return RecognizeCards(image, y, height);
    }

    private static List<string> RecognizeCards(Bitmap image, int y, int height)
    {
        float baseWidth = 1920f * image.Height / 1080f;
        int width = (int)(0.121f * baseWidth);
        int gap = (int)(0.0053f * baseWidth);
        for (int count = 4; count >= 1; count--)
        {
            List<Bitmap> pieces = new List<Bitmap>();
            try
            {
                int start = image.Width / 2 - (width * count + gap * (count - 1)) / 2;
                for (int i = 0; i < count; i++)
                {
                    Rectangle area = new Rectangle(start + i * (width + gap), y, width, height);
                    pieces.Add(GoldText(image, area, 2, 150, 115, 35, 20));
                    pieces.Add(GoldText(image, area, 1, 95, 72, 17, 8));
                    pieces.Add(CropAndScale(image, area, 4));
                }
                List<string> guesses = RelicHelper.RecognizeRawBatch(pieces);
                List<string> names = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    string[] observations = guesses.Skip(i * 3).Take(3).ToArray();
                    names.Add(RelicHelper.ResolveEvidence(observations));
                    Debug("count=" + count + " card=" + i + " variants=" +
                        String.Join(" || ", observations.Select(value => value.Replace("\n", " "))) +
                        " resolved=" + names[i]);
                }
                if (names.All(name => name != null)) return names;
            }
            finally { foreach (Bitmap piece in pieces) piece.Dispose(); }
        }
        throw new InvalidOperationException("仍有中文奖励无法可靠匹配物品词库");
    }

    private static Bitmap GoldText(Bitmap image, Rectangle area, int scale,
        int minRed = 115, int minGreen = 90, int redBlue = 25, int greenBlue = 15)
    {
        if (area.X < 0 || area.Y < 0 || area.Right > image.Width || area.Bottom > image.Height)
            throw new InvalidOperationException("奖励文字区域超出截图");
        using (Bitmap mask = new Bitmap(area.Width, area.Height))
        {
            for (int y = 0; y < area.Height; y++)
                for (int x = 0; x < area.Width; x++)
                {
                    Color color = image.GetPixel(area.X + x, area.Y + y);
                    bool gold = color.R > minRed && color.G > minGreen &&
                        color.R > color.B + redBlue && color.G > color.B + greenBlue;
                    mask.SetPixel(x, y, gold ? Color.Black : Color.White);
                }
            if (scale == 1) return (Bitmap)mask.Clone();
            Bitmap enlarged = new Bitmap(area.Width * scale, area.Height * scale);
            using (Graphics graphics = Graphics.FromImage(enlarged))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.DrawImage(mask, new Rectangle(0, 0, enlarged.Width, enlarged.Height));
            }
            return enlarged;
        }
    }

    private static Bitmap CropAndScale(Bitmap image, Rectangle area, int scale)
    {
        Bitmap cropped = new Bitmap(area.Width * scale, area.Height * scale);
        using (Graphics graphics = Graphics.FromImage(cropped))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(image, new Rectangle(0, 0, cropped.Width, cropped.Height),
                area, GraphicsUnit.Pixel);
        }
        return cropped;
    }

    private static void Debug(string value)
    {
        if (Environment.GetEnvironmentVariable("ALECAFRAME_ZH_OCR_DEBUG") == "1")
            Console.Error.WriteLine(value);
    }

    private static List<string> RecognizeStrip(Bitmap image, out int estimated)
    {
        estimated = 4;
        Rectangle area = new Rectangle(image.Width / 4, (int)(image.Height * 0.37f),
            image.Width / 2, (int)(image.Height * 0.065f));
        using (Bitmap strip = new Bitmap(area.Width, area.Height))
        using (Graphics graphics = Graphics.FromImage(strip))
        {
            graphics.DrawImage(image, new Rectangle(0, 0, strip.Width, strip.Height),
                area, GraphicsUnit.Pixel);
            string result = RelicHelper.RecognizeRaw(strip);
            int count = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Count(Char.IsLetterOrDigit) >= 5);
            if (count >= 1 && count <= 4) estimated = count;
            if (Environment.GetEnvironmentVariable("ALECAFRAME_ZH_OCR_DEBUG") == "1")
                Console.Error.WriteLine("strip OCR=" + result.Replace("\r", " ").Replace("\n", " | "));
            return RelicHelper.ResolveLines(result);
        }
    }

    private static List<string> Recognize(Bitmap image, int count, bool legacy)
    {
        float scale = legacy && image.Height > 1080 ? 0.74f : 1f;
        float top = legacy ? 0.4027f : 0.38f;
        float bottom = legacy ? 0.437f : 0.4271f;
        float fractionWidth = legacy ? 0.1005f : 0.121f;
        float fractionGap = legacy ? 0.0052f : 0.0053f;
        top = 0.5f + scale * (top - 0.5f);
        bottom = 0.5f + scale * (bottom - 0.5f);
        float baseWidth = 1920f * image.Height / 1080f;
        int width = (int)(fractionWidth * baseWidth * scale);
        int gap = (int)(fractionGap * baseWidth * scale);
        int y = (int)(image.Height * top);
        int height = (int)(image.Height * bottom) - y;
        int start = image.Width / 2 - (width * count + gap * (count - 1)) / 2;
        List<Bitmap> pieces = new List<Bitmap>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                int x = start + i * (width + gap);
                if (x < 0 || x + width > image.Width || y < 0 || y + height > image.Height)
                    throw new InvalidOperationException("截图区域超出边界");
                int half = height / 2;
                foreach (int offset in new[] { 0, half })
                {
                    Bitmap piece = new Bitmap(width, half);
                    using (Graphics graphics = Graphics.FromImage(piece))
                        graphics.DrawImage(image, new Rectangle(0, 0, width, half),
                            new Rectangle(x, y + offset, width, half), GraphicsUnit.Pixel);
                    pieces.Add(piece);
                }
            }
            return RelicHelper.Recognize(pieces);
        }
        finally { foreach (Bitmap piece in pieces) piece.Dispose(); }
    }

    private static void EnsureData()
    {
        if (parts != null) return;
        languages = JObject.Parse(File.ReadAllText(Path.Combine(DataRoot, "lang.json"), Encoding.UTF8));
        JArray componentData = JArray.Parse(File.ReadAllText(Path.Combine(DataRoot, "Components.json"), Encoding.UTF8));
        parts = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject part in componentData)
        {
            string name = (string)part.SelectToken("drops[0].type");
            if (!String.IsNullOrEmpty(name) && !parts.ContainsKey(name)) parts.Add(name, part);
        }
        parents = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(DataRoot, "*.json"))
        {
            string label = Path.GetFileName(file);
            if (label == "lang.json" || label == "Components.json") continue;
            try
            {
                JArray items = JArray.Parse(File.ReadAllText(file, Encoding.UTF8));
                foreach (JObject item in items.OfType<JObject>())
                {
                    string key = (string)item["uniqueName"];
                    if (key != null && !parents.ContainsKey(key)) parents.Add(key, item);
                }
            }
            catch (JsonException) { }
        }
    }

    private static Reward MakeReward(string english)
    {
        JObject part;
        if (!parts.TryGetValue(english, out part))
            throw new InvalidOperationException("物品表缺少：" + english);
        string partKey = (string)part["uniqueName"];
        string parentKey = (string)part.SelectToken("parentUniqueNames[0]");
        JObject parent = null;
        if (parentKey != null) parents.TryGetValue(parentKey, out parent);
        JToken parentLanguage = parentKey == null ? null : languages[parentKey];
        JToken partLanguage = partKey == null ? null : languages[partKey];
        string baseZh = parentLanguage == null ? null : (string)parentLanguage.SelectToken("zh.name");
        string suffix = partLanguage == null ? null : (string)partLanguage.SelectToken("zh.name");
        if (String.IsNullOrWhiteSpace(suffix) && (string)part["name"] == "Blueprint") suffix = "蓝图";
        else if (!String.IsNullOrWhiteSpace(suffix) && english.EndsWith(" Blueprint",
            StringComparison.OrdinalIgnoreCase) && suffix != "蓝图") suffix += " 蓝图";
        int count = 1;
        JArray components = parent == null ? null : parent["components"] as JArray;
        if (components != null)
        {
            JToken recipe = components.FirstOrDefault(c => (string)c["uniqueName"] == partKey);
            if (recipe != null && recipe["itemCount"] != null) count = (int)recipe["itemCount"];
        }
        return new Reward
        {
            englishName = (string)part.SelectToken("drops[0].type") ?? english,
            name = RelicHelper.GetDisplayName((string)part.SelectToken("drops[0].type") ?? english,
                String.IsNullOrEmpty(baseZh) ? english : baseZh + " " + suffix),
            internalName = partKey,
            parentName = parent == null ? null : (string)parent["name"],
            parentInternalName = parentKey,
            ducats = part["ducats"] == null ? -1 : (int)part["ducats"],
            totalToOwn = count,
            isItemVaulted = parent != null && parent["vaulted"] != null && (bool)parent["vaulted"]
        };
    }
}
