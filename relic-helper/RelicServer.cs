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
        public int quantity;
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
        int expectedCount;
        List<Reward> rewards = ReadLogWithCount(path, out expectedCount);
        WriteResult(new { ok = true, sourceMtime, expectedCount, rewards });
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
        int expectedCount;
        return ReadLogWithCount(path, out expectedCount);
    }

    private static List<Reward> ReadLogWithCount(string path, out int expectedCount)
    {
        expectedCount = 0;
        using (ZipArchive zip = ZipFile.OpenRead(path))
        {
            ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("original.jpeg", StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new InvalidOperationException("遗物记录没有原始截图");
            using (Stream imageStream = entry.Open())
            using (Bitmap original = new Bitmap(imageStream))
            {
                RewardLayout layout = RelicLayout.FindRewards(original);
                expectedCount = layout.Names.Length;
                int[] quantities;
                List<string> names = RelicHelper.RecognizeRewardCards(original, layout, out quantities);
                if (names.Count != expectedCount) throw new InvalidOperationException("奖励识别数量与画面卡片数量不一致");
                EnsureData();
                return names.Select((name, index) => MakeReward(name, quantities[index])).ToList();
            }
        }
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

    private static Reward MakeReward(string english, int quantity)
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
            name = (quantity > 1 ? quantity + "× " : "") + RelicHelper.GetDisplayName((string)part.SelectToken("drops[0].type") ?? english,
                String.IsNullOrEmpty(baseZh) ? english : baseZh + " " + suffix),
            internalName = partKey,
            parentName = parent == null ? null : (string)parent["name"],
            parentInternalName = parentKey,
            quantity = quantity,
            ducats = part["ducats"] == null ? -1 : (int)part["ducats"],
            totalToOwn = count,
            isItemVaulted = parent != null && parent["vaulted"] != null && (bool)parent["vaulted"]
        };
    }
}
