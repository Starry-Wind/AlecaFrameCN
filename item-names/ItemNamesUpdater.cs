using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlecaFrameZh
{
    internal static class ItemNamesUpdater
    {
        internal const string Endpoint = "https://api.warframe.market/v2/items";
        private static readonly Regex BuildVersion = new Regex(@"^\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}$");
        private static readonly Regex Spaces = new Regex(@"\s+");
        private static readonly Regex FramePart = new Regex(@"^(.*?) (Set|Neuroptics Blueprint|Chassis Blueprint|Systems Blueprint|Neuroptics|Chassis|Systems|Blueprint)$");
        private static readonly Dictionary<string, string> FrameSuffixes = new Dictionary<string, string> {
            { "Set", "一套" }, { "Blueprint", "蓝图" },
            { "Neuroptics", "头部神经光元" }, { "Chassis", "机体" }, { "Systems", "系统" },
            { "Neuroptics Blueprint", "头部神经光元 蓝图" }, { "Chassis Blueprint", "机体 蓝图" }, { "Systems Blueprint", "系统 蓝图" }
        };

        internal static string Key(string name) { return Spaces.Replace(name.Trim(), " ").ToLowerInvariant(); }
        private static bool Name(string value) { return !String.IsNullOrWhiteSpace(value) && value.Length <= 256 && value.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0; }
        private static bool MarketName(string value) { return Name(value) && value.IndexOfAny(new[] { '<', '>' }) < 0; }
        private static string LanguageName(JToken token, string language)
        {
            var languages = token as JObject;
            var translated = languages == null ? null : languages[language] as JObject;
            return translated == null ? null : (string)translated["name"];
        }
        private static string LocalName(JObject languages, string id) { return LanguageName(languages[id ?? ""], "zh"); }

        // One localized response contains both English and Chinese for each ID.
        // No login, cookies, orders or account information are requested.
        internal static JObject Download()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "GET";
            request.UserAgent = "AlecaFrameCN-ItemNames/1.0";
            request.Accept = "application/json";
            request.Headers["Language"] = "zh-hans";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("物品接口未返回成功响应。");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                var buffer = new byte[8192];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > 16 * 1024 * 1024 || DateTime.UtcNow > deadline) throw new InvalidDataException("物品接口响应过大或读取超时。");
                    output.Write(buffer, 0, count);
                }
                return JObject.Parse(Encoding.UTF8.GetString(output.ToArray()));
            }
        }

        internal static JObject Merge(JObject baseline, JObject catalogue, string cacheRoot)
        {
            var rows = catalogue["data"] as JArray;
            int previousCount = (int?)baseline["marketItemCount"] ?? (int?)baseline["sourceRows"] ?? 0;
            if (catalogue["error"] != null && catalogue["error"].Type != JTokenType.Null) throw new InvalidDataException("物品接口返回错误。");
            if (rows == null || rows.Count < Math.Max(1000, previousCount * 8 / 10)) throw new InvalidDataException("物品目录为空、不完整或格式已变化。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
            var market = new List<JObject>();
            foreach (var row in rows.OfType<JObject>())
            {
                string id = (string)row["id"], en = LanguageName(row["i18n"], "en"), zh = LanguageName(row["i18n"], "zh-hans");
                if (!Name(id) || !MarketName(en) || !MarketName(zh)) continue;
                if (!ids.Add(id)) throw new InvalidDataException("物品目录包含重复标识。");
                string key = Key(en);
                Tuple<string, string> old;
                if (pairs.TryGetValue(key, out old) && old.Item2 != zh) throw new InvalidDataException("同一英文名称对应了不同中文名称。");
                pairs[key] = Tuple.Create(en.Trim(), zh.Trim());
                market.Add(row);
            }
            if (market.Count < rows.Count * 9 / 10) throw new InvalidDataException("物品目录缺少过多中英文名称。");

            var names = new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
            var aliases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var frames = new Dictionary<string, string>(StringComparer.Ordinal);
            Action<string, string> alias = (chinese, english) => {
                if (!Name(chinese) || !Name(english) || Key(chinese) == Key(english)) return;
                HashSet<string> choices;
                if (!aliases.TryGetValue(Key(chinese), out choices)) aliases[Key(chinese)] = choices = new HashSet<string>(StringComparer.Ordinal);
                choices.Add(Key(english));
            };
            Action<string, string, bool> put = (en, zh, overwrite) => {
                if (!Name(en) || !Name(zh)) return;
                string key = Key(en);
                Tuple<string, string> old;
                if (names.TryGetValue(key, out old)) alias(old.Item2, old.Item1);
                if (overwrite || old == null || old.Item1 == old.Item2) names[key] = Tuple.Create(en.Trim(), zh.Trim());
                alias(zh, en);
            };
            foreach (var p in ((JObject)baseline["items"]).Properties()) put(p.Name, (string)p.Value, true);
            foreach (var p in ((JObject)baseline["aliases"]).Properties()) alias(p.Name, (string)p.Value);
            foreach (string en in baseline["warframes"].Values<string>()) frames[Key(en)] = en;

            // Only sets/parts identify Warframes. Augment mods also carry the
            // "warframe" tag and must keep their normal Chinese translations.
            foreach (var row in market)
            {
                var tags = row["tags"] as JArray;
                if (tags == null || !tags.Values<string>().Contains("warframe") || tags.Values<string>().Contains("mod")) continue;
                string en = (string)row["i18n"]["en"]["name"];
                var part = FramePart.Match(en);
                if (part.Success) frames[Key(part.Groups[1].Value)] = part.Groups[1].Value;
            }
            var records = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            JObject languages = new JObject();
            if (Directory.Exists(cacheRoot))
            {
                foreach (string file in Directory.GetFiles(cacheRoot, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    try
                    {
                        var content = JToken.Parse(File.ReadAllText(file, Encoding.UTF8));
                        string category = Path.GetFileNameWithoutExtension(file);
                        if (category == "lang") { languages = content as JObject ?? new JObject(); continue; }
                        var entries = content as JArray;
                        if (entries == null) continue;
                        foreach (var row in entries.OfType<JObject>())
                        {
                            string id = (string)row["uniqueName"], en = (string)row["name"];
                            if (!Name(id) || !Name(en)) continue;
                            records[id] = row; categories[id] = category;
                            if (category == "Warframes") frames[Key(en)] = en;
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { } // The official cache may be updating.
                }
            }
            foreach (var entry in records)
            {
                var row = entry.Value;
                string en = (string)row["name"], zh = LocalName(languages, entry.Key) ?? en, category = categories[entry.Key];
                var parents = row["parentUniqueNames"] as JArray;
                if (category == "Components" && parents != null)
                {
                    foreach (string id in parents.Values<string>())
                    {
                        JObject parent;
                        if (!records.TryGetValue(id, out parent)) continue;
                        string parentEn = (string)parent["name"], parentZh = LocalName(languages, id) ?? parentEn;
                        string translatedParent = frames.ContainsKey(Key(parentEn)) ? parentEn : parentZh;
                        string suffix = LocalName(languages, entry.Key) ?? (en == "Blueprint" ? "蓝图" : en);
                        string fullEn = parentEn + " " + en, fullZh = translatedParent + " " + suffix;
                        put(fullEn, fullZh, false); alias(parentZh + " " + suffix, fullEn);
                        if (!fullEn.EndsWith(" Blueprint", StringComparison.Ordinal)) { put(fullEn + " Blueprint", fullZh + " 蓝图", false); alias(parentZh + " " + suffix + " 蓝图", fullEn + " Blueprint"); }
                        var drops = row["drops"] as JArray;
                        string dropEn = drops != null && drops.Count > 0 ? (string)drops[0]["type"] : null;
                        if (Name(dropEn)) put(dropEn, fullZh + (dropEn.EndsWith(" Blueprint", StringComparison.Ordinal) && en != "Blueprint" ? " 蓝图" : ""), false);
                    }
                }
                else
                {
                    put(en, frames.ContainsKey(Key(en)) ? en : zh, false);
                    if (new[] { "Warframes", "Primary", "Secondary", "Melee", "Archwing", "Arch-Gun", "Arch-Melee" }.Contains(category))
                        put(en + " Blueprint", (frames.ContainsKey(Key(en)) ? en : zh) + " 蓝图", false);
                }
            }
            foreach (var pair in pairs.Values) put(pair.Item1, pair.Item2, true);
            // Protect frame base names and frame prefixes even if a future
            // market translation starts using translated Warframe names.
            foreach (var key in names.Keys.ToArray())
            {
                var name = names[key];
                if (frames.ContainsKey(key)) { names[key] = Tuple.Create(name.Item1, frames[key]); continue; }
                var part = FramePart.Match(name.Item1);
                if (!part.Success || !frames.ContainsKey(Key(part.Groups[1].Value))) continue;
                string frame = frames[Key(part.Groups[1].Value)];
                if (!name.Item2.StartsWith(frame + " ", StringComparison.Ordinal))
                    names[key] = Tuple.Create(name.Item1, frame + " " + FrameSuffixes[part.Groups[2].Value]);
            }
            // Keep existing spelling variants and the game's icon tokens
            // (e.g. <ARCHWING>). Remote market names are validated separately.
            var items = (JObject)baseline["items"].DeepClone();
            foreach (var p in items.Properties())
            {
                Tuple<string, string> name;
                if (names.TryGetValue(Key(p.Name), out name)) p.Value = name.Item2;
            }
            foreach (var name in names.OrderBy(p => p.Key, StringComparer.Ordinal)) items[name.Value.Item1] = name.Value.Item2;
            var mappedAliases = new JObject();
            foreach (var a in aliases.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (a.Value.Count != 1) continue;
                Tuple<string, string> target;
                if (names.TryGetValue(a.Value.First(), out target) && target.Item1 != target.Item2) mappedAliases[a.Key] = target.Item1;
            }
            var result = (JObject)baseline.DeepClone();
            result["source"] = Endpoint;
            result["sourceRows"] = market.Count;
            result["marketItemCount"] = market.Count;
            result["apiVersion"] = (string)catalogue["apiVersion"];
            result["items"] = items; result["aliases"] = mappedAliases;
            result["warframes"] = new JArray(frames.Values.OrderBy(f => f, StringComparer.Ordinal));
            return result;
        }

        internal static string DetectWarframeVersion(string logRoot)
        {
            var executables = new List<string>();
            string loggedVersion = null;
            var logs = new[] { "EE.log", "Preprocess.log", "Launcher.log" }
                .Select(name => Path.Combine(logRoot, name)).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (string log in logs)
            {
                try
                {
                    using (var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(input, Encoding.UTF8, true))
                    {
                        // Only startup headers are needed; never read trade or account data.
                        for (int i = 0; i < (Path.GetFileName(log) == "Launcher.log" ? 512 : 32); i++)
                        {
                            string line = reader.ReadLine();
                            if (line == null) break;
                            var build = Regex.Match(line, @"Build Label:\s*(\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2})\b");
                            if (loggedVersion == null && build.Success) loggedVersion = build.Groups[1].Value;
                            var directory = Regex.Match(line, @"Current directory:\s*(.+?)\s*$");
                            if (directory.Success)
                            {
                                string path = directory.Groups[1].Value;
                                if (Path.IsPathRooted(path) && !path.StartsWith(@"\", StringComparison.Ordinal))
                                {
                                    executables.Add(Path.GetFullPath(Path.Combine(path, "Warframe.x64.exe")));
                                    executables.Add(Path.GetFullPath(Path.Combine(path, "..", "Warframe.x64.exe")));
                                }
                            }
                            var executable = Regex.Match(line, "\"([A-Za-z]:\\\\[^\"\\r\\n]+\\\\Warframe\\.x64\\.exe)\"");
                            if (executable.Success) executables.Add(executable.Groups[1].Value);
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            // An installed update may be newer than the last launch log.
            foreach (string executable in executables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(executable)) continue;
                    var info = FileVersionInfo.GetVersionInfo(executable);
                    foreach (string version in new[] { info.FileVersion, info.ProductVersion })
                        if (version != null && BuildVersion.IsMatch(version.Trim())) return version.Trim();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return loggedVersion;
        }
        private static void AtomicWrite(string path, JObject data, string backup)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, data.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, backup); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static string Run(string packageRoot, string cacheRoot, string warframeVersion, Func<JObject> download)
        {
            string dictionary = Path.Combine(packageRoot, "item-names", "item-names.json");
            var baseline = JObject.Parse(File.ReadAllText(dictionary, Encoding.UTF8));
            if (warframeVersion == null || !BuildVersion.IsMatch(warframeVersion)) return "未检测到 Warframe 游戏版本，继续使用现有词库，下次启动时再次检测。";
            if ((string)baseline["warframeVersion"] == warframeVersion) return "Warframe " + warframeVersion + " 与词库版本一致，无需联网更新。";
            try
            {
                var result = Merge(baseline, download(), cacheRoot);
                result.Remove("warframeVersion");
                result.AddFirst(new JProperty("warframeVersion", warframeVersion));
                result["updatedAtUtc"] = DateTime.UtcNow.ToString("o");
                var before = (JObject)baseline["items"];
                var after = (JObject)result["items"];
                int added = after.Properties().Count(p => before[p.Name] == null);
                int changed = after.Properties().Count(p => before[p.Name] != null && !JToken.DeepEquals(before[p.Name], p.Value));
                string build = Path.Combine(packageRoot, "build");
                Directory.CreateDirectory(build);
                AtomicWrite(dictionary, result, Path.Combine(build, "item-names.previous.json"));
                return "物品词库已更新至 Warframe " + warframeVersion + "：市场 " + result["marketItemCount"] + " 项，新增 " + added + " 个名称，更新 " + changed + " 个译名。";
            }
            catch (Exception error)
            {
                return "Warframe " + warframeVersion + " 的物品词库更新未完成，保留原词库及版本，下次启动重试：" + error.Message;
            }
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length != 3) return 1;
            string root = Path.GetFullPath(args[0]);
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
            using (var mutex = new Mutex(false, "Local\\AlecaFrameCNItemNames-" + BitConverter.ToString(hash).Replace("-", "")))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { Console.WriteLine("已有物品词库更新正在进行，本次使用现有词库。"); return 0; }
                try { Console.WriteLine(Run(root, args[1], DetectWarframeVersion(args[2]), Download)); return 0; }
                catch (Exception error) { Console.WriteLine("物品词库检查未完成：" + error.Message); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
