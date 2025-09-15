using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ArkRoxBot.Services
{
    public sealed class InventoryService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _botSteamId64;

        public InventoryService(string botSteamId64)
        {
            _http = new HttpClient();
            _botSteamId64 = botSteamId64 ?? string.Empty;
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        /// <summary>
        /// Full-frame TF2 inventory snapshot via the community inventory endpoint.
        /// Keeps the whole inventory; counts keys & metal and returns a timestamp.
        /// </summary>
        public async Task<InventorySnapshot> GetSnapshotAsync()
        {
            InventorySnapshot snapshot = new InventorySnapshot();

            if (string.IsNullOrWhiteSpace(_botSteamId64))
                return snapshot;

            // TF2 app=440, context=2
            string url = "https://steamcommunity.com/inventory/" + _botSteamId64 + "/440/2?l=en&count=5000";

            HttpResponseMessage resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine("[Inventory] HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                return snapshot;
            }

            string json = await resp.Content.ReadAsStringAsync();
            InventoryResponse? parsed = JsonConvert.DeserializeObject<InventoryResponse>(json);
            if (parsed == null || parsed.assets == null || parsed.descriptions == null)
            {
                Console.WriteLine("[Inventory] Empty or malformed response.");
                return snapshot;
            }

            Dictionary<string, InvDescription> descIndex = BuildDescriptionIndex(parsed.descriptions);

            foreach (InvAsset a in parsed.assets)
            {
                if (a.appid != 440) continue; // TF2 only

                string key = a.classid + "_" + a.instanceid;
                string name = descIndex.TryGetValue(key, out InvDescription d) && !string.IsNullOrEmpty(d.name)
                    ? d.name
                    : key;

                int count = 1;
                if (!string.IsNullOrEmpty(a.amount))
                {
                    int parsedAmt;
                    if (int.TryParse(a.amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedAmt) && parsedAmt > 0)
                        count = parsedAmt;
                }

                if (string.Equals(name, "Mann Co. Supply Crate Key", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.Keys += count;
                }
                else if (string.Equals(name, "Refined Metal", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.Refined += count;
                }
                else if (string.Equals(name, "Reclaimed Metal", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.Reclaimed += count;
                }
                else if (string.Equals(name, "Scrap Metal", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.Scrap += count;
                }
            }

            snapshot.FetchedUtc = DateTime.UtcNow;
            return snapshot;
        }

        private static Dictionary<string, InvDescription> BuildDescriptionIndex(List<InvDescription> descriptions)
        {
            Dictionary<string, InvDescription> dict = new Dictionary<string, InvDescription>(StringComparer.Ordinal);
            if (descriptions == null) return dict;

            foreach (InvDescription d in descriptions)
            {
                string key = d.classid + "_" + d.instanceid;
                if (!dict.ContainsKey(key))
                    dict[key] = d;
            }
            return dict;
        }
    }

    // ===== Models for community inventory =====

    internal sealed class InventoryResponse
    {
        public List<InvAsset> assets { get; set; } = new List<InvAsset>();
        public List<InvDescription> descriptions { get; set; } = new List<InvDescription>();
        public bool more { get; set; }
        public string? last_assetid { get; set; }
    }

    internal sealed class InvAsset
    {
        public int appid { get; set; }
        public string contextid { get; set; } = string.Empty;
        public string assetid { get; set; } = string.Empty;
        public string classid { get; set; } = string.Empty;
        public string instanceid { get; set; } = string.Empty;
        public string amount { get; set; } = "1";
    }

    internal sealed class InvDescription
    {
        public string classid { get; set; } = string.Empty;
        public string instanceid { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
    }

    public sealed class InventorySnapshot
    {
        public int Keys { get; set; }
        public int Refined { get; set; }
        public int Reclaimed { get; set; }
        public int Scrap { get; set; }
        public DateTime FetchedUtc { get; set; }
    }
}
