using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArkRoxBot.Models;
using ArkRoxBot.Models.Config;
using Newtonsoft.Json;

namespace ArkRoxBot.Services
{
    public sealed class TradeService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly PriceStore _priceStore;
        private readonly ItemConfigLoader _configLoader;
        private readonly string _apiKey;
        private readonly string _botSteamId64;

        private System.Threading.Timer? _timer;
        private System.Threading.CancellationTokenSource? _cts;
        private Task? _loopTask;

        public TradeService(PriceStore priceStore,
                            ItemConfigLoader configLoader,
                            string apiKey,
                            string botSteamId64)
        {
            _http = new HttpClient();
            _priceStore = priceStore;
            _configLoader = configLoader;
            _apiKey = apiKey ?? string.Empty;
            _botSteamId64 = botSteamId64 ?? string.Empty;
        }

        // in TradeService

        private async Task PollAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            // TODO: implement polling/validation/acceptance here.
            // For now this is just a no-op so your timer compiles and runs cleanly.
            // You can log something if you want:
            // Console.WriteLine("[Trade] Poll tick…");

            await Task.CompletedTask;
        }
        public void Start()
        {
            if (_timer != null) return;
            _cts = new System.Threading.CancellationTokenSource();

            // example: poll every 30s
            _timer = new System.Threading.Timer(async _ =>
            {
                try { await PollAsync(_cts.Token); } catch { /* log if you like */ }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        private async Task RunLoopAsync()
        {
            // poll every 30 seconds
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Trade] Poll error: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                }
                catch (TaskCanceledException)
                {
                    // shutdown
                }
            }
        }

        private async Task PollOnceAsync()
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2 * 24 * 60 * 60; // 2 days
            string url = "https://api.steampowered.com/IEconService/GetTradeOffers/v1/?" +
                         "key=" + Uri.EscapeDataString(_apiKey) +
                         "&get_received_offers=1&active_only=1&get_descriptions=1&language=en_us" +
                         "&time_historical_cutoff=" + cutoff.ToString(CultureInfo.InvariantCulture);

            HttpResponseMessage resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine("[Trade] GetTradeOffers HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                return;
            }

            string json = await resp.Content.ReadAsStringAsync();
            GetTradeOffersResponse? parsed = JsonConvert.DeserializeObject<GetTradeOffersResponse>(json);
            if (parsed == null || parsed.response == null)
            {
                Console.WriteLine("[Trade] Empty trade offers response.");
                return;
            }

            Dictionary<string, Description> descIndex = BuildDescriptionIndex(parsed.response.descriptions);

            if (parsed.response.trade_offers_received == null)
            {
                return;
            }

            ConfigRoot cfg = _configLoader.LoadItems();

            foreach (TradeOffer offer in parsed.response.trade_offers_received)
            {
                // 2 = Active, 9 = Confirmation Needed (still treat as active), others skip for now
                if (offer.trade_offer_state != 2 && offer.trade_offer_state != 9)
                    continue;

                string partner64 = AccountIdToSteamId64(offer.accountid_other);

                // Only consider offers to our account (received offers already are)
                OfferSummary summary = SummarizeOffer(offer, descIndex);

                // Evaluate vs PriceStore + items.json
                decimal receiveValueRef = 0m;
                foreach (KeyValuePair<string, int> kv in summary.ItemsToReceiveByName)
                {
                    string name = kv.Key;
                    int count = kv.Value;

                    PriceResult priced;
                    if (_priceStore.TryGetPrice(name, out priced) && priced.MostCommonSellPrice > 0m)
                    {
                        receiveValueRef += priced.MostCommonSellPrice * count;
                    }
                    else
                    {
                        // unknown item -> treat as zero for safety
                    }
                }

                decimal giveValueRef = 0m;
                foreach (KeyValuePair<string, int> kv in summary.ItemsToGiveByName)
                {
                    string name = kv.Key;
                    int count = kv.Value;

                    PriceResult priced;
                    if (_priceStore.TryGetPrice(name, out priced) && priced.MostCommonBuyPrice > 0m)
                    {
                        giveValueRef += priced.MostCommonBuyPrice * count;
                    }
                    else
                    {
                        // unknown -> zero
                    }
                }

                // Simple decision: profitRef = what we can likely sell for - what we pay by our buy rules
                decimal profitRef = receiveValueRef - giveValueRef;

                Console.WriteLine("[Trade] Offer " + offer.tradeofferid +
                                  " from " + partner64 +
                                  " | recv=" + receiveValueRef.ToString("0.00") +
                                  " ref, give=" + giveValueRef.ToString("0.00") +
                                  " ref, profit=" + profitRef.ToString("0.00") + " ref");

                Console.WriteLine("         +" + FormatDict(summary.ItemsToReceiveByName));
                Console.WriteLine("         -" + FormatDict(summary.ItemsToGiveByName));

                // For Step 2A we ONLY log. In Step 2B, we will accept/decline based on profitRef and config bounds.
            }
        }

        private static string AccountIdToSteamId64(int accountId)
        {
            // 76561197960265728 is SteamID64 base
            long id64 = 76561197960265728L + (long)accountId;
            return id64.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, Description> BuildDescriptionIndex(List<Description>? descriptions)
        {
            Dictionary<string, Description> map = new Dictionary<string, Description>();
            if (descriptions == null)
                return map;

            foreach (Description d in descriptions)
            {
                string key = d.classid + "_" + d.instanceid;
                map[key] = d;
            }
            return map;
        }

        private static OfferSummary SummarizeOffer(TradeOffer offer, Dictionary<string, Description> descIndex)
        {
            OfferSummary s = new OfferSummary();

            if (offer.items_to_receive != null)
            {
                foreach (Asset a in offer.items_to_receive)
                {
                    if (a.appid != 440) continue; // TF2 only
                    string dkey = a.classid + "_" + a.instanceid;
                    string name = descIndex.TryGetValue(dkey, out Description d) && !string.IsNullOrEmpty(d.name)
                        ? d.name
                        : dkey;

                    int count = 1;
                    int parsed;
                    if (!string.IsNullOrEmpty(a.amount) && int.TryParse(a.amount, out parsed))
                        count = parsed;

                    if (!s.ItemsToReceiveByName.ContainsKey(name))
                        s.ItemsToReceiveByName[name] = 0;

                    s.ItemsToReceiveByName[name] += count;
                }
            }

            if (offer.items_to_give != null)
            {
                foreach (Asset a in offer.items_to_give)
                {
                    if (a.appid != 440) continue;
                    string dkey = a.classid + "_" + a.instanceid;
                    string name = descIndex.TryGetValue(dkey, out Description d) && !string.IsNullOrEmpty(d.name)
                        ? d.name
                        : dkey;

                    int count = 1;
                    int parsed;
                    if (!string.IsNullOrEmpty(a.amount) && int.TryParse(a.amount, out parsed))
                        count = parsed;

                    if (!s.ItemsToGiveByName.ContainsKey(name))
                        s.ItemsToGiveByName[name] = 0;

                    s.ItemsToGiveByName[name] += count;
                }
            }

            return s;
        }

        private static string FormatDict(Dictionary<string, int> map)
        {
            if (map.Count == 0) return "(none)";
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in map)
            {
                parts.Add(kv.Key + " x" + kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(", ", parts);
        }

        public void Stop()
        {
            // Atomically grab and clear the CTS (so Stop/Dispose can be called multiple times)
            System.Threading.CancellationTokenSource? cts =
                System.Threading.Interlocked.Exchange(ref _cts, null);

            if (cts == null)
            {
                // Never started (or already stopped) — nothing to do
                return;
            }

            try { cts.Cancel(); } catch { }

            try
            {
                // If you use a timer-based poller
                if (_timer != null)
                {
                    _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    _timer.Dispose();
                    _timer = null;
                }
            }
            catch { }

            try
            {
                // If you use a loop task instead of a timer, wait briefly
                _loopTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            try { _http.CancelPendingRequests(); } catch { }

            cts.Dispose();
        }

        public void Dispose()
        {
            // Safe even if Start() never ran
            Stop();

            // HttpClient is always created in ctor, so it’s safe to dispose
            _http.Dispose();
        }


        // --------- DTOs for GetTradeOffers ---------

        private sealed class GetTradeOffersResponse
        {
            public InnerResponse? response { get; set; }
        }

        private sealed class InnerResponse
        {
            public List<TradeOffer>? trade_offers_received { get; set; }
            public List<Description>? descriptions { get; set; }
        }

        private sealed class TradeOffer
        {
            public string tradeofferid { get; set; } = string.Empty;
            public int accountid_other { get; set; }
            public int trade_offer_state { get; set; }
            public List<Asset>? items_to_receive { get; set; }
            public List<Asset>? items_to_give { get; set; }
        }

        private sealed class Asset
        {
            public int appid { get; set; }
            public string contextid { get; set; } = string.Empty;
            public string assetid { get; set; } = string.Empty;
            public string classid { get; set; } = string.Empty;
            public string instanceid { get; set; } = string.Empty;
            public string amount { get; set; } = "1";
        }

        private sealed class Description
        {
            public string appid { get; set; } = string.Empty;
            public string classid { get; set; } = string.Empty;
            public string instanceid { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
            public string market_hash_name { get; set; } = string.Empty;
        }

        private sealed class OfferSummary
        {
            public Dictionary<string, int> ItemsToReceiveByName { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, int> ItemsToGiveByName { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
