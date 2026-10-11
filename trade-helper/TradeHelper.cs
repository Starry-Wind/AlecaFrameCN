using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AlecaFramePublicLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlecaFrameZh
{
    // Only the trade pipeline calls this helper. Game language and relic OCR
    // remain under the control of the official plugin / existing relic patch.
    public static class TradeHelper
    {
        // The override is used only by offline tests / replay, never by the launcher.
        private static readonly string LocalRoot = Environment.GetEnvironmentVariable("ALECAFRAME_ZH_TEST_LOCAL_ROOT") ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly object DataLock = new object();
        private static Dictionary<string, List<Item>> aliases;
        private static Dictionary<string, Item> items;
        private const string DialogPrefix = "Dialog::CreateOkCancel(description=";
        private static readonly Regex Header = new Regex(
            @"^(?:(?:你|您)?(?:是否)?(?:确定|確定|确认|確認)|是否).*?(?:接受|进行|進行|同意|确认|確認).*?交易.*?(?:你|您)(?:将|將|正在)?(?:会|會)?(?:提供|给予|給予|交出|付出|送出|给出|給出|支付)(?:的(?:道具|物品)(?:是|为|為))?\s*[：:]?\s*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex[] ReceiveHeaders = new Regex[] {
            // Verified in live Simplified Chinese EE.log: no "以下物品" suffix.
            new Regex(@"^而你将会从\s+(?<user>.+?)\s+处收到\s*[：:]\s*$"),
            new Regex(@"^(?:(?:并且|並且|并|並|而)\s*)?(?:你|您)?\s*(?:将|將)?\s*(?:会|會)?\s*(?:从|從)\s*(?<user>.+?)\s*(?:那里|那裡|处|處)?\s*(?:获得|獲得|收到|接收|得到)\s*(?:以下|下列)(?:的)?(?:物品|东西|東西|内容|內容)?\s*[：:]?\s*$"),
            new Regex(@"^(?:(?:并且|並且|并|並|而)\s*)?(?:你|您)?\s*(?:将|將)?\s*(?:会|會)?\s*(?:收到|接收|获得|獲得)\s*(?:来自|來自)\s*(?<user>.+?)\s*的\s*(?:以下|下列)(?:物品|东西|東西|内容|內容)?\s*[：:]?\s*$"),
            new Regex(@"^(?:(?:并且|並且|并|並|而)\s*)?(?:你|您)?\s*(?:将|將)?\s*(?:会|會)?\s*(?:收到|接收|获得|獲得)\s*(?<user>.+?)\s*的\s*(?:以下|下列)(?:物品|东西|東西|内容|內容)?\s*[：:]?\s*$")
        };
        private static readonly Regex Quantity = new Regex(@"^(?<name>.+?)\s*[xX×]\s*(?<count>[0-9][0-9,，]*)\s*$");
        private static readonly Regex Rank = new Regex(@"\s*[（(]\s*(?:(?:RIVEN\s*RANK|MOD\s*RANK|RANK|裂罅(?:\s*MOD)?(?:等级|等級)?|紫卡(?:等级|等級)?|(?:MOD\s*)?(?:等级|等級))\s*[:：]?\s*)?(?<rank>[0-9]+)\s*[）)]\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex Icons = new Regex(@"<[^>]+>|[\uE000-\uF8FF]");

        private sealed class Item
        {
            public string id;
            public string english;
            public string chinese;
            public string category;
        }

        public static bool IsChineseGame()
        {
            // Read only: do not change Launcher.log or AlecaFrame's lastLang.tmp.
            try
            {
                string path = Path.Combine(LocalRoot, "Warframe", "Launcher.log");
                if (File.Exists(path))
                {
                    using (var reader = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                    {
                        string log = reader.ReadToEnd();
                        int position = log.LastIndexOf("-language:", StringComparison.Ordinal);
                        if (position >= 0)
                        {
                            Match code = Regex.Match(log.Substring(position + 10), @"^[a-zA-Z-]+");
                            return IsChineseCode(code.Value);
                        }
                    }
                }
                return IsChineseCode(File.ReadAllText(Path.Combine(LocalRoot, "AlecaFrame", "lastLang.tmp")).Trim());
            }
            catch { return false; }
        }

        private static bool IsChineseCode(string code)
        {
            return code.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                code.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("tc", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsChineseTradeBeginning(string message)
        {
            if (String.IsNullOrEmpty(message) || !message.Contains(DialogPrefix)) return false;
            string description = Description(message);
            string first = description.Split('\n')[0].Trim();
            return Header.IsMatch(first);
        }

        // Called at the entry to the official EELogProcessor, before its
        // existing success check. Chat text / failed trades cannot match.
        public static string NormalizeLogLine(string message)
        {
            if (String.IsNullOrEmpty(message) || !IsChineseGame()) return message;
            if (!message.Contains("[Info]") || !message.Contains("Dialog::Create") || !message.Contains("description=")) return message;
            string description = Description(message).Trim();
            if (Regex.IsMatch(description, @"^(?:交易成功|交易已成功|交易已完成|交易完成|交易已成功完成|成功完成交易)[!！。.]?$"))
            {
                Log("检测到中文交易成功提示");
                return message.Replace(description, "The trade was successful!, ");
            }
            return message;
        }

        public static bool IsChineseTradeCancellation(string message)
        {
            if (String.IsNullOrEmpty(message) || !IsChineseGame() || !message.Contains("Dialog::Create") || !message.Contains("description=")) return false;
            return Regex.IsMatch(Description(message).Trim(), @"^(?:交易(?:已)?(?:取消|失败|失敗)|(?:对方|對方)(?:已)?取消了?交易)[!！。.]?$");
        }

        private static string Description(string message)
        {
            int start = message.IndexOf("description=", StringComparison.Ordinal);
            string text = start < 0 ? message : message.Substring(start + 12);
            // A multiline log may put the dialog arguments on its last line.
            int end = text.IndexOf(", title=", StringComparison.Ordinal);
            if (end >= 0) text = text.Substring(0, end);
            return text.Replace("\r", "").Replace("\\n", "\n");
        }

        public static PlayerStatsTrade ParseChineseTrade(List<string> lines)
        {
            try { return ParseTradeText(String.Join("\n", lines)); }
            catch (Exception ex)
            {
                Log("中文交易解析失败，未提交：" + ex.Message);
                return null;
            }
        }

        // Public for offline replay. This method never submits a trade.
        public static PlayerStatsTrade ParseTradeText(string text)
        {
            string[] lines = Description(text).Split('\n');
            if (lines.Length < 3 || !Header.IsMatch(lines[0].Trim()))
                throw new InvalidDataException("未识别确认交易标题");
            var trade = new PlayerStatsTrade { ts = DateTime.UtcNow };
            bool receiving = false;
            foreach (string raw in lines.Skip(1))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                Match boundary = ReceiveHeaders.Select(r => r.Match(line)).FirstOrDefault(m => m.Success);
                if (boundary != null)
                {
                    if (receiving) throw new InvalidDataException("出现多个接收物品标题");
                    // Warframe appends a platform icon (e.g. U+E000) to names.
                    trade.user = Icons.Replace(boundary.Groups["user"].Value, "").Trim();
                    if (trade.user.Length == 0 || trade.user.Any(Char.IsWhiteSpace))
                        throw new InvalidDataException("交易对象格式不正确");
                    receiving = true;
                    continue;
                }
                if (line == "无" || line == "無" || line == "没有" || line == "None") continue;
                line = line.Normalize(NormalizationForm.FormKC);
                Match quantity = Quantity.Match(line);
                int count = 1;
                if (quantity.Success && (!Int32.TryParse(quantity.Groups["count"].Value.Replace(",", "").Replace("，", ""),
                    NumberStyles.None, CultureInfo.InvariantCulture, out count) || count <= 0))
                    throw new InvalidDataException("物品数量不正确");
                string name = quantity.Success ? quantity.Groups["name"].Value.Trim() : line;
                if (String.IsNullOrWhiteSpace(name) || name.Contains("[Info]") || name.Contains("description="))
                    throw new InvalidDataException("物品行格式不正确");
                List<PlayerStatsTradeTradedObjectInfo> side = receiving ? trade.rx : trade.tx;
                PlayerStatsTradeTradedObjectInfo existing = side.FirstOrDefault(p => p.name == name);
                if (existing == null) side.Add(new PlayerStatsTradeTradedObjectInfo { name = name, displayName = name, cnt = count, rank = -1 });
                else existing.cnt = checked(existing.cnt + count);
            }
            // Fail closed if the receive heading changes. Never record all
            // received items as things the player sold.
            if (!receiving || trade.tx.Count == 0 || trade.rx.Count == 0 || trade.tx.Count > 6 || trade.rx.Count > 6)
                throw new InvalidDataException("交易两侧物品不完整，或未识别接收物品标题");
            Log("中文交易已解析：发送 " + trade.tx.Count + " 类，接收 " + trade.rx.Count + " 类");
            return trade;
        }

        public static bool ConvertChineseItem(PlayerStatsTradeTradedObjectInfo value)
        {
            string original = value.name;
            value.rank = -1;
            try
            {
                string name = original.Normalize(NormalizationForm.FormKC).Trim();
                Match rank = Rank.Match(name);
                if (rank.Success)
                {
                    value.rank = Int32.Parse(rank.Groups["rank"].Value, CultureInfo.InvariantCulture);
                    name = name.Substring(0, rank.Index).Trim();
                }
                name = Icons.Replace(name, "").Trim();
                if (name == "白金" || name == "铂金" || name == "鉑金" || name.Equals("Platinum", StringComparison.OrdinalIgnoreCase) || name == "plat")
                    return Assign(value, "/AF_Special/Platinum", "白金");
                if (name == "传说核心" || name == "傳說核心" || name == "Legendary Core")
                    return Assign(value, "/AF_Special/Legendary Fusion Core", name);
                EnsureData();
                Item match = Find(name);
                if (match != null) return Assign(value, match.id, match.chinese ?? match.english);
                // Fish sizes use the same character codes as the official parser.
                Match size = Regex.Match(name, @"^(?<name>.+?)\s*\((?<size>小|中|大|S|M|L)\)$");
                if (size.Success)
                {
                    match = Find(size.Groups["name"].Value);
                    if (match != null && match.category == "Fish")
                    {
                        string symbol = size.Groups["size"].Value;
                        value.rank = (symbol == "小" ? 'S' : symbol == "中" ? 'M' : symbol == "大" ? 'L' : symbol[0]);
                        return Assign(value, match.id, match.chinese ?? match.english);
                    }
                }
                // Preserve the weapon's official English name in the existing
                // special riven ID scheme. Do not guess an ambiguous weapon.
                Match riven = Regex.Match(name, @"^(?<weapon>.+?)\s+(?<suffix>[A-Za-z]+(?:-[A-Za-z]+)+)$");
                if (riven.Success)
                {
                    match = Find(riven.Groups["weapon"].Value);
                    if (match != null && new [] { "Primary", "Secondary", "Melee", "Arch-Gun", "Arch-Melee", "SentinelWeapons" }.Contains(match.category))
                        return Assign(value, "/AF_Special/Riven/" + match.english + "/" + riven.Groups["suffix"].Value, name);
                }
            }
            catch (Exception ex) { Log("中文物品词库读取失败：" + ex.Message); }
            value.name = "/AF_Special/Other/" + original;
            value.displayName = original;
            Log("未匹配交易物品，保留原名：" + original);
            return false;
        }

        private static bool Assign(PlayerStatsTradeTradedObjectInfo value, string id, string display)
        {
            value.name = id;
            value.displayName = display;
            return true;
        }

        private static string Key(string name)
        {
            return Regex.Replace(Icons.Replace(name.Normalize(NormalizationForm.FormKC), ""), @"\s+", "").ToLowerInvariant();
        }

        private static Item Find(string name)
        {
            List<Item> candidates;
            if (!aliases.TryGetValue(Key(name), out candidates)) return null;
            if (candidates.Count == 1) return candidates[0];
            // The exported dataset includes tutorial versions with identical
            // labels. The normal tradable mod is outside Beginner/Intermediate.
            var normal = candidates.Where(p => !p.id.Contains("/Beginner/") && !p.id.Contains("/Intermediate/")).ToList();
            if (normal.Count == 1) return normal[0];
            var veiled = normal.Where(p => p.id.Contains("/Raw")).ToList();
            if (veiled.Count == 1) return veiled[0];
            return null;
        }

        private static void AddAlias(string name, Item item, Dictionary<string, List<Item>> index)
        {
            if (String.IsNullOrWhiteSpace(name)) return;
            string key = Key(name);
            List<Item> values;
            if (!index.TryGetValue(key, out values)) index.Add(key, values = new List<Item>());
            if (!values.Any(p => p.id == item.id)) values.Add(item);
        }

        private static string LocalName(JObject language, string id, string code)
        {
            JToken value = language[id];
            return value == null ? null : (string)value.SelectToken(code + ".name");
        }

        private static void EnsureData()
        {
            lock (DataLock)
            {
                if (aliases != null) return;
                string root = Path.Combine(LocalRoot, "AlecaFrame", "cachedData", "json");
                JObject languages = JObject.Parse(File.ReadAllText(Path.Combine(root, "lang.json"), Encoding.UTF8));
                var records = new Dictionary<string, JObject>();
                var categories = new Dictionary<string, string>();
                foreach (string file in Directory.GetFiles(root, "*.json").OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (Path.GetFileName(file) == "lang.json") continue;
                    JArray data = JToken.Parse(File.ReadAllText(file, Encoding.UTF8)) as JArray;
                    if (data == null) continue;
                    foreach (JObject record in data.OfType<JObject>())
                    {
                        string id = (string)record["uniqueName"];
                        if (String.IsNullOrEmpty(id)) continue;
                        records[id] = record;
                        categories[id] = Path.GetFileNameWithoutExtension(file);
                    }
                }
                var nextItems = new Dictionary<string, Item>();
                var nextAliases = new Dictionary<string, List<Item>>();
                foreach (var pair in records)
                {
                    string id = pair.Key;
                    JObject record = pair.Value;
                    string en = (string)record["name"];
                    if (String.IsNullOrWhiteSpace(en)) continue;
                    string zh = LocalName(languages, id, "zh");
                    string tc = LocalName(languages, id, "tc");
                    var item = new Item { id = id, english = en, chinese = zh, category = categories[id] };
                    JArray parentIds = record["parentUniqueNames"] as JArray;
                    if (item.category == "Components" && parentIds != null)
                    {
                        // The language export calls a component just "机体" or
                        // "枪管"; join it with its parent before indexing.
                        foreach (string parentId in parentIds.Values<string>())
                        {
                            JObject parent;
                            if (!records.TryGetValue(parentId, out parent)) continue;
                            string parentEn = (string)parent["name"];
                            string parentZh = LocalName(languages, parentId, "zh") ?? parentEn;
                            string parentTc = LocalName(languages, parentId, "tc") ?? parentEn;
                            string fullEn = JoinName(parentEn, en);
                            string fullZh = JoinName(parentZh, zh ?? en);
                            string fullTc = JoinName(parentTc, tc ?? zh ?? en);
                            if (nextItems.ContainsKey(id) == false)
                            {
                                item.english = fullEn;
                                item.chinese = fullZh;
                            }
                            AddAlias(fullEn, item, nextAliases);
                            AddAlias(fullEn + " Blueprint", item, nextAliases);
                            AddAlias(fullZh, item, nextAliases);
                            AddAlias(fullZh + " 蓝图", item, nextAliases);
                            AddAlias(fullTc, item, nextAliases);
                            AddAlias(fullTc + " 藍圖", item, nextAliases);
                            nextItems[id] = item;
                        }
                    }
                    else
                    {
                        AddAlias(en, item, nextAliases);
                        AddAlias(zh, item, nextAliases);
                        AddAlias(tc, item, nextAliases);
                        if (new [] { "Warframes", "Primary", "Secondary", "Melee", "Archwing", "Arch-Gun", "Arch-Melee" }.Contains(item.category))
                        {
                            AddAlias(en + " Blueprint", item, nextAliases);
                            AddAlias((zh ?? en) + " 蓝图", item, nextAliases);
                            AddAlias((tc ?? zh ?? en) + " 藍圖", item, nextAliases);
                        }
                        nextItems[id] = item;
                    }
                }
                items = nextItems;
                aliases = nextAliases; // Publish the complete index atomically.
            }
        }

        private static string JoinName(string parent, string part)
        {
            if (String.IsNullOrEmpty(part)) return parent;
            if (Key(part).StartsWith(Key(parent), StringComparison.Ordinal)) return part;
            return parent + " " + part.TrimStart('-', ' ');
        }

        public static string GetTradeDisplayNames(string idsJson)
        {
            try
            {
                if (idsJson == "[]") return "{}";
                EnsureData();
                var result = new Dictionary<string, string>();
                foreach (string id in JsonConvert.DeserializeObject<string[]>(idsJson).Distinct())
                {
                    Item item;
                    if (id != null && items.TryGetValue(id, out item) && !String.IsNullOrEmpty(item.chinese)) result[id] = item.chinese;
                    else if (id == "/AF_Special/Platinum") result[id] = "白金";
                }
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex) { Log("交易名称汉化失败：" + ex.Message); return "{}"; }
        }

        private static void Log(string message)
        {
            try
            {
                string root = Path.Combine(LocalRoot, "AlecaFrame-ZH-Patch");
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "trade-patch.log"), DateTime.Now.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
