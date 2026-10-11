using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AlecaFrameZh
{
    // Shared display dictionary. IDs and the game's own name aliases stay intact.
    internal static class ItemNames
    {
        private static readonly Dictionary<string, string> Names = Load();

        private static string Key(string name)
        {
            return Regex.Replace(name.Trim(), @"\s+", " ").ToLowerInvariant();
        }

        private static Dictionary<string, string> Load()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(ItemNames).Assembly.Location), "item-names.json");
            JObject data = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in ((JObject)data["items"]).Properties()) result[Key(pair.Name)] = (string)pair.Value;
            foreach (string name in data["warframes"].Values<string>()) result[Key(name)] = name;
            return result;
        }

        public static string Chinese(string english, string fallback)
        {
            string translated;
            return english != null && Names.TryGetValue(Key(english), out translated) ? translated : fallback;
        }
    }
}
