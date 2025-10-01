using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Models.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArkRoxBot.Services
{
    public sealed class TradeService : IDisposable, ITradeService
    {
        private readonly OfferEvaluator _evaluator;
        private readonly bool _tradingEnabled;
        private readonly bool _dryRun;
        private readonly HttpClient _http;
        private readonly PriceStore _priceStore;
        private readonly ItemConfigLoader _configLoader;
        private readonly string _apiKey;
        private readonly string _botSteamId64;
        private readonly bool _verifySellAssets;

        private readonly HttpClient _community;
        private readonly CookieContainer _cookieJar = new CookieContainer();

        private System.Threading.Timer? _timer;
        private System.Threading.CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _isPolling = 0;

        private const decimal AcceptToleranceRef = 0.02m;
        private decimal _sellTolRef;

        // Trusted partners; runtime toggle with !trust
        private readonly HashSet<string> _trustedPartners = new HashSet<string>(StringComparer.Ordinal);
        private volatile bool _trustedAcceptEnabled = false;

        private PureSnapshot? _pureCache;
        private DateTime _pureCacheUtc;

        public TradeService(
            PriceStore priceStore,
            ItemConfigLoader configLoader,
            string apiKey,
            string botSteamId64,
            OfferEvaluator evaluator)
        {
            _priceStore = priceStore ?? throw new ArgumentNullException(nameof(priceStore));
            _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
            _apiKey = apiKey ?? string.Empty;
            _botSteamId64 = botSteamId64 ?? string.Empty;
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

            // ---- General API client
            HttpClientHandler apiHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(apiHandler);
            _http.Timeout = TimeSpan.FromSeconds(30);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ArkRoxBot/1.0");

            // ---- Community client
            HttpClientHandler communityHandler = new HttpClientHandler
            {
                CookieContainer = _cookieJar,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = false,
                UseCookies = true
            };
            _community = new HttpClient(communityHandler);
            _community.Timeout = TimeSpan.FromSeconds(30);
            _community.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
            _community.DefaultRequestHeaders.UserAgent.ParseAdd("ArkRoxBot/1.0");
            _community.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _community.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            LoadCommunityCookiesFromEnvDual();
            DumpCommunityCookieDebug();

            // ---- Flags
            _tradingEnabled = string.Equals(
                Environment.GetEnvironmentVariable("TRADING_ENABLED"), "true",
                StringComparison.OrdinalIgnoreCase);

            _dryRun = !string.Equals(
                Environment.GetEnvironmentVariable("DRY_RUN"), "false",
                StringComparison.OrdinalIgnoreCase);

            _verifySellAssets = !string.Equals(
                Environment.GetEnvironmentVariable("VERIFY_SELL_ASSETS"), "false",
                StringComparison.OrdinalIgnoreCase);

            string tolRaw = Environment.GetEnvironmentVariable("SELL_TOL_REF") ?? string.Empty;
            if (!decimal.TryParse(tolRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out _sellTolRef))
                _sellTolRef = AcceptToleranceRef;

            Console.WriteLine("[Trade] Flags → TRADING_ENABLED=" + _tradingEnabled +
                              ", DRY_RUN=" + _dryRun +
                              ", VERIFY_SELL_ASSETS=" + _verifySellAssets +
                              ", SELL_TOL_REF=" + _sellTolRef.ToString(CultureInfo.InvariantCulture));

            // ---- Trusted partners
            string trustedRaw = Environment.GetEnvironmentVariable("TRUSTED_STEAMIDS") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(trustedRaw))
            {
                string[] parts = trustedRaw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string id in parts)
                    if (id.Length >= 17) _trustedPartners.Add(id.Trim());
            }
            _trustedAcceptEnabled = string.Equals(Environment.GetEnvironmentVariable("TRUSTED_ACCEPT_ENABLED"), "true",
                StringComparison.OrdinalIgnoreCase);

            if (_trustedPartners.Count > 0)
            {
                Console.WriteLine("[Trade] Trusted partners: " + string.Join(",", _trustedPartners));
                Console.WriteLine("[Trade] Trusted auto-accept initially " + (_trustedAcceptEnabled ? "ENABLED" : "DISABLED") + ".");
            }
        }

        // ---------------- Lifecycle ----------------

        public void Start()
        {
            if (_timer != null) return;
            _cts = new CancellationTokenSource();

            _timer = new System.Threading.Timer(async _ =>
            {
                try { await PollAsync(_cts.Token); }
                catch (Exception ex) { Console.WriteLine("[Trade] Timer poll error: " + ex.Message); }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        public void Stop()
        {
            CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
            if (cts == null) return;

            try { cts.Cancel(); } catch { }

            try
            {
                if (_timer != null)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    _timer.Dispose();
                    _timer = null;
                }
            }
            catch { }

            try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _http.CancelPendingRequests(); } catch { }

            cts.Dispose();
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
        }

        // ---------------- Trusted auto-accept ----------------

        public void SetTrustedAcceptEnabled(bool enabled)
        {
            _trustedAcceptEnabled = enabled;
            Console.WriteLine("[Trust] Trusted auto-accept " + (enabled ? "ENABLED" : "DISABLED") + ".");
        }

        public bool GetTrustedAcceptEnabled() => _trustedAcceptEnabled;

        private async Task<bool> TryTrustedAutoAcceptAsync(string offerId, string partnerSteamId64)
        {
            if (!_trustedAcceptEnabled) return false;
            if (!_trustedPartners.Contains(partnerSteamId64)) return false;

            Console.WriteLine("[Trust] Offer " + offerId + " from " + partnerSteamId64 + " → auto-accept (trusted).");

            if (!_tradingEnabled) { Console.WriteLine("[Trust] TRADING_ENABLED=false → would accept (trusted)."); return true; }
            if (_dryRun) { Console.WriteLine("[Trust] DRY_RUN=true → would accept (trusted)."); return true; }

            bool ok = await AcceptOfferCommunityAsync(offerId, partnerSteamId64);
            if (!ok) Console.WriteLine("[Trust] Accept failed for trusted partner; check cookies / Steam status.");
            return true; // handled
        }

        // ---------------- Polling ----------------

        private async Task PollAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (Interlocked.Exchange(ref _isPolling, 1) == 1) return;

            try { await PollOnceAsync(); }
            catch (Exception ex) { Console.WriteLine("[Trade] Poll error: " + ex.Message); }
            finally { Volatile.Write(ref _isPolling, 0); }
        }

        public Task LogStockSnapshotOnceAsync() => LogStockSnapshotAsync();

        private async Task LogStockSnapshotAsync()
        {
            try
            {
                PureSnapshot pure = await GetPureSnapshotAsync();
                Console.WriteLine("[Stock] Pure → Keys=" + pure.Keys + ", Ref=" + pure.Refined +
                                  ", Rec=" + pure.Reclaimed + ", Scrap=" + pure.Scrap);

                ConfigRoot cfg = _configLoader.LoadItems();
                HashSet<string> names = new HashSet<string>(cfg.SellConfig.Select(s => StripLeadingThe(s.Name)), NameCmp);
                Dictionary<string, int> have = await GetCountsForNamesAsync(names);
                foreach (var kv in have.OrderBy(k => k.Key))
                    Console.WriteLine("[Stock] " + kv.Key + ": " + kv.Value);
            }
            catch (Exception ex) { Console.WriteLine("[Stock] Snapshot failed: " + ex.Message); }
        }

        private async Task PollOnceAsync()
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2 * 24 * 60 * 60;
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
            if (parsed == null || parsed.response == null) { Console.WriteLine("[Trade] Empty trade offers response."); return; }

            Dictionary<string, Description> descIndex = BuildDescriptionIndex(parsed.response.descriptions);
            if (parsed.response.trade_offers_received == null) return;

            ConfigRoot cfg = _configLoader.LoadItems();

            foreach (TradeOffer offer in parsed.response.trade_offers_received)
            {
                if (offer.trade_offer_state != 2 && offer.trade_offer_state != 9) continue;

                string partner64 = AccountIdToSteamId64(offer.accountid_other);
                OfferSummary summary = SummarizeOffer(offer, descIndex);
                LogOfferSummary(offer.tradeofferid, partner64, summary);

                if (offer.trade_offer_state == 9)
                {
                    Console.WriteLine("[Trade] Offer " + offer.tradeofferid + " needs confirmation → skipping for now.");
                    continue;
                }

                if (await TryTrustedAutoAcceptAsync(offer.tradeofferid, partner64)) continue;

                string policyReason;
                bool policyOk = CheckBasicPolicy(summary, cfg, out policyReason);
                if (!policyOk)
                {
                    Console.WriteLine("[Policy] Decline: " + policyReason);
                    if (_tradingEnabled && !_dryRun)
                        try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                    else
                        Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + policyReason + ")");
                    continue;
                }

                bool givesNonPure = summary.ItemsToGiveByName.Keys.Any(n => !IsPureName(n));
                bool receivesOnlyPure = OnlyPure(summary.ItemsToReceiveByName);
                bool receivesNonPure = summary.ItemsToReceiveByName.Keys.Any(n => !IsPureName(n));
                bool givesOnlyPure = OnlyPure(summary.ItemsToGiveByName);

                // SELL: we give items, receive only pure
                if (givesNonPure && receivesOnlyPure)
                {
                    if (_verifySellAssets)
                    {
                        var sellAssetsCheck = await CheckSellAssetsAvailableAsync(offer.items_to_give);
                        if (!sellAssetsCheck.Ok)
                        {
                            Console.WriteLine("[Policy] Decline (sell assets): " + sellAssetsCheck.Reason);
                            if (_tradingEnabled && !_dryRun)
                                try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                            else
                                Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (sell assets)");
                            continue;
                        }
                    }

                    decimal requiredRef = 0m;
                    foreach (var kv in summary.ItemsToGiveByName)
                    {
                        string name = kv.Key;
                        int qty = kv.Value;
                        if (IsPureName(name)) continue;

                        PriceResult price;
                        if (!_priceStore.TryGetPrice(name, out price) || price.MostCommonSellPrice <= 0m)
                        {
                            Console.WriteLine("[Policy] Decline: missing SELL price for " + name);
                            if (_tradingEnabled && !_dryRun)
                                try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                            else
                                Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (missing SELL price)");
                            goto NextOffer;
                        }

                        requiredRef += price.MostCommonSellPrice * qty;
                    }

                    decimal offeredRef = RefFromPure(summary.ItemsToReceiveByName, GetKeyRefPrice());
                    if (offeredRef + _sellTolRef >= requiredRef)
                    {
                        Console.WriteLine("[Eval] SELL ok | offered=" + offeredRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                          " ref | required=" + requiredRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref");

                        if (_tradingEnabled && !_dryRun)
                        {
                            try { await AcceptOfferCommunityAsync(offer.tradeofferid, partner64); }
                            catch (Exception ex) { Console.WriteLine("[Trade] Accept error: " + ex.Message); }
                        }
                        else
                        {
                            Console.WriteLine("[Trade] DRY-RUN: would Accept offer " + offer.tradeofferid + " (sell)");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[Policy] Decline: offered " + offeredRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                          " ref < required SELL " + requiredRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref");
                        if (_tradingEnabled && !_dryRun)
                            try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                        else
                            Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (underpay)");
                    }

                    goto NextOffer;
                }

                // BUY: we receive non-pure, give only pure
                if (receivesNonPure && givesOnlyPure)
                {
                    var capCheck = await CheckStockCapsAsync(summary, cfg);
                    if (!capCheck.Ok)
                    {
                        Console.WriteLine("[Policy] Decline (stock cap): " + capCheck.Reason);
                        if (_tradingEnabled && !_dryRun)
                            try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                        else
                            Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + capCheck.Reason + ")");
                        goto NextOffer;
                    }

                    var pureCheck = await CheckPureBalanceAsync(summary);
                    if (!pureCheck.Ok)
                    {
                        Console.WriteLine("[Policy] Decline (pure): " + pureCheck.Reason);
                        if (_tradingEnabled && !_dryRun)
                            try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                        else
                            Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + pureCheck.Reason + ")");
                        goto NextOffer;
                    }

                    OfferEvaluationResult ev = _evaluator.Evaluate(summary);
                    Console.WriteLine("[Eval] " + ev.Decision +
                                      " | recv=" + ev.ReceiveRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                      " | give=" + ev.GiveRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                      " | profit=" + ev.ProfitRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                      " | reason=" + ev.Reason);

                    if (_tradingEnabled && !_dryRun)
                    {
                        try
                        {
                            if (ev.Decision == OfferDecision.Accept) await AcceptOfferCommunityAsync(offer.tradeofferid, partner64);
                            else await DeclineOfferCommunityAsync(offer.tradeofferid);
                        }
                        catch (Exception ex) { Console.WriteLine("[Trade] Action error for " + offer.tradeofferid + ": " + ex.Message); }
                    }
                    else
                    {
                        Console.WriteLine("[Trade] DRY-RUN: would " + ev.Decision + " offer " + offer.tradeofferid + " (" + ev.Reason + ")");
                    }

                    goto NextOffer;
                }

                // Mixed / unsupported
                {
                    const string why = "unsupported composition (mixed non-pure on both sides)";
                    Console.WriteLine("[Policy] Decline: " + why);
                    if (_tradingEnabled && !_dryRun)
                        try { await DeclineOfferCommunityAsync(offer.tradeofferid); } catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                    else
                        Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + why + ")");
                }

            NextOffer:
                continue;
            }
        }

        private async Task<(bool Ok, string Reason)> CheckStockCapsAsync(OfferSummary summary, ConfigRoot cfg)
        {
            // Which non-pure items would we add?
            var toReceive = new Dictionary<string, int>(NameCmp);
            foreach (var kv in summary.ItemsToReceiveByName)
                if (!IsPureName(kv.Key))
                    toReceive[kv.Key] = kv.Value;

            if (toReceive.Count == 0)
                return (true, string.Empty);

            // Max quantities from config (BuyConfig)
            var maxByName = new Dictionary<string, int>(NameCmp);
            foreach (var b in cfg.BuyConfig)
                if (!string.IsNullOrWhiteSpace(b.Name))
                    maxByName[b.Name] = b.MaxQuantity;

            // Current counts of just these names
            var current = await GetCountsForNamesAsync(toReceive.Keys);

            foreach (var kv in toReceive)
            {
                string name = kv.Key;
                int need = kv.Value;

                if (!maxByName.TryGetValue(name, out int max) || max <= 0)
                    continue; // not capped or not tracked for caps

                int have = current.TryGetValue(name, out int h) ? h : 0;

                if (have + need > max)
                {
                    string reason = $"stock cap for {name}: have {have}, need {need}, max {max}";
                    return (false, reason);
                }
            }

            return (true, string.Empty);
        }


        // ---------------- Community accept/decline ----------------

        private async Task<int> PreflightOfferPageAsync(string offerId)
        {
            string url = "https://steamcommunity.com/tradeoffer/" + offerId + "/";
            try
            {
                HttpResponseMessage resp = await _community.GetAsync(url);
                int code = (int)resp.StatusCode;
                string loc = resp.Headers.Location != null ? resp.Headers.Location.ToString() : string.Empty;

                if (code == 302 && loc.IndexOf("/market/eligibilitycheck", StringComparison.OrdinalIgnoreCase) >= 0)
                    Console.WriteLine("[Trade] Preflight → eligibilitycheck (OK to proceed).");

                if (code != 200 && code != 204)
                    Console.WriteLine("[Trade] Preflight offer page " + offerId + " HTTP " + code + (string.IsNullOrEmpty(loc) ? "" : " → " + loc));

                return code;
            }
            catch (Exception ex) { Console.WriteLine("[Trade] Preflight offer page error: " + ex.Message); return -1; }
        }

        private async Task<bool> AcceptOfferCommunityAsync(string offerId, string partnerSteamId64)
        {
            await PreflightOfferPageAsync(offerId);
            string url = "https://steamcommunity.com/tradeoffer/" + offerId + "/accept";
            string referer = "https://steamcommunity.com/tradeoffer/" + offerId + "/";
            string sess = GetCookieValue("https://steamcommunity.com", "sessionid") ?? string.Empty;

            Dictionary<string, string> form = new Dictionary<string, string>
            {
                { "sessionid", sess },
                { "serverid", "1" },
                { "tradeofferid", offerId },
                { "partner", partnerSteamId64 },
                { "captcha", "" }
            };

            return await SendCommunityPostWithRetriesAsync(url, form, referer, "Community accept", offerId);
        }

        private async Task<bool> DeclineOfferCommunityAsync(string offerId)
        {
            string url = "https://steamcommunity.com/tradeoffer/" + offerId + "/decline";
            string referer = "https://steamcommunity.com/tradeoffer/" + offerId + "/";
            string sess = GetCookieValue("https://steamcommunity.com", "sessionid") ?? string.Empty;

            Dictionary<string, string> form = new Dictionary<string, string>
            {
                { "sessionid", sess },
                { "serverid", "1" }
            };

            return await SendCommunityPostWithRetriesAsync(url, form, referer, "Community decline", offerId);
        }

        private static readonly int[] CommunityTransient = { 429, 500, 502, 503, 504 };

        private async Task<bool> SendCommunityPostWithRetriesAsync(
            string url, Dictionary<string, string> form, string referer, string actionLabel, string offerId)
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Referrer = new Uri(referer);
                    req.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
                    req.Content = new FormUrlEncodedContent(form);

                    HttpResponseMessage resp = await _community.SendAsync(req);
                    int code = (int)resp.StatusCode;
                    string body = await SafeReadSnippetAsync(resp);
                    bool maybeE502 = code == 502 || body.IndexOf("E502", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isTransient = Array.IndexOf(CommunityTransient, code) >= 0 || maybeE502;

                    if (resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine("[Trade] " + actionLabel + " OK for " + offerId + ".");
                        return true;
                    }

                    if ((code == 302 || code == 401 || code == 403) && attempt == 1)
                    {
                        Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " HTTP " + code + " → cookies invalid?");
                        return false;
                    }

                    if (isTransient && attempt < 4)
                    {
                        int delayMs = 400 * attempt;
                        Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " transient " + code + " → retry in " + delayMs + " ms.");
                        await Task.Delay(delayMs);
                        continue;
                    }

                    Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " failed HTTP " + code + " → " + body);
                    return false;
                }
                catch (Exception ex)
                {
                    if (attempt < 4)
                    {
                        int delayMs = 400 * attempt;
                        Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " error: " + ex.Message +
                                          " → retry in " + delayMs + " ms.");
                        await Task.Delay(delayMs);
                        continue;
                    }
                    Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " error: " + ex.Message + " (giving up)");
                    return false;
                }
            }

            return false;
        }

        private static async Task<string> SafeReadSnippetAsync(HttpResponseMessage resp, int max = 400)
        {
            try
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (text.Length > max) return text.Substring(0, max) + "…";
                return text;
            }
            catch { return "(no body)"; }
        }

        // ---------------- Outbound offer (send) ----------------

        private sealed class OfferMakeAsset
        {
            public string appid { get; set; } = "440";
            public string contextid { get; set; } = "2";
            public string assetid { get; set; } = string.Empty;
            public int amount { get; set; } = 1;
        }

        private static string Steam64ToAccountIdParam(string steamId64)
        {
            long id64 = long.Parse(steamId64, CultureInfo.InvariantCulture);
            long accountId = id64 - 76561197960265728L;
            return accountId.ToString(CultureInfo.InvariantCulture);
        }

        private async Task<(bool Ok, string? OfferId, string? Error)> SendTradeOfferAsync(
            string partnerSteamId64,
            List<OfferMakeAsset> meGive,
            List<OfferMakeAsset> themGive,
            string? message,
            string? accessToken)
        {
            // Preflight the new-offer page
            string partnerParam = Steam64ToAccountIdParam(partnerSteamId64);
            string preflight = "https://steamcommunity.com/tradeoffer/new/?partner=" + partnerParam;
            try { await _community.GetAsync(preflight); } catch { /* ignore */ }

            string sess = GetCookieValue("https://steamcommunity.com", "sessionid")
                          ?? (Environment.GetEnvironmentVariable("STEAM_SESSIONID") ?? string.Empty);
            if (string.IsNullOrEmpty(sess)) return (false, null, "missing sessionid cookie");

            string jsonOffer = JsonConvert.SerializeObject(new
            {
                newversion = true,
                version = 4,
                me = new { assets = meGive, currency = Array.Empty<object>(), ready = false },
                them = new { assets = themGive, currency = Array.Empty<object>(), ready = false }
            });

            string createParams = string.IsNullOrEmpty(accessToken)
                ? "{}"
                : "{\"trade_offer_access_token\":\"" + accessToken + "\"}";

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/tradeoffer/new/send"))
            {
                req.Headers.Referrer = new Uri(preflight);
                req.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                Dictionary<string, string> form = new Dictionary<string, string>
                {
                    { "sessionid", sess },
                    { "serverid", "1" },
                    { "partner", partnerSteamId64 },
                    { "tradeoffermessage", message ?? string.Empty },
                    { "json_tradeoffer", jsonOffer },
                    { "captcha", string.Empty },
                    { "trade_offer_create_params", createParams }
                };
                req.Content = new FormUrlEncodedContent(form);

                HttpResponseMessage resp = await _community.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine("[Offer] send HTTP " + ((int)resp.StatusCode) + " → " + TrimForLog(body));
                    return (false, null, body);
                }

                try
                {
                    JObject jo = JObject.Parse(body);
                    string id = (string?)jo["tradeofferid"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(id))
                    {
                        Console.WriteLine("[Offer] created id " + id);
                        return (true, id, null);
                    }
                }
                catch { /* ignore parse */ }

                Console.WriteLine("[Offer] send OK but unexpected payload: " + TrimForLog(body));
                return (true, null, null);
            }
        }

        // Public helper used by chat (!sell <item>)
        public async Task<(bool Ok, string? OfferId, string Reason)> CreateBuyOfferAsync(string partnerSteamId64, string itemName, decimal payRef)
        {
            InvResponse? partnerInv = await FetchPartnerInventoryAsync(partnerSteamId64);
            if (partnerInv == null) return (false, null, "partner inventory unavailable");

            string? itemAssetId = FindAssetIdByName(partnerInv, itemName);
            if (string.IsNullOrEmpty(itemAssetId)) return (false, null, "partner does not have the item");

            (int needRef, int needRec, int needScrap) = BreakRef(payRef);
            List<OfferMakeAsset>? mePay = await PickOurMetalAsync(needRef, needRec, needScrap);
            if (mePay == null) return (false, null, "not enough pure to pay");

            List<OfferMakeAsset> themGive = new List<OfferMakeAsset> { new OfferMakeAsset { assetid = itemAssetId } };

            return await SendTradeOfferAsync(
                partnerSteamId64,
                mePay,
                themGive,
                "Buying '" + itemName + "' for " + payRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref",
                null);
        }

        public async Task<(bool Ok, string? OfferId, string Reason)> CreateSellOfferAsync(
            string partnerSteamId64, string itemName, decimal priceRef)
        {
            // 1) Make sure we own the item we’re selling
            var ourAssets = await FindOurItemAssetsAsync(itemName, 1);
            if (ourAssets.Count == 0)
                return (false, null, "we do not have '" + itemName + "' in our inventory");

            // 2) Look at partner’s inventory for pure (ref/rec/scrap) to cover the price
            InvResponse? partnerInv = await FetchPartnerInventoryAsync(partnerSteamId64);
            if (partnerInv == null)
                return (false, null, "partner inventory unavailable");

            var pool = CollectPure(partnerInv);

            // For simplicity, require exact metal (no keys) for now
            var (needRef, needRec, needScrap) = BreakRef(priceRef);

            var themPay = new List<OfferMakeAsset>();
            TakeFirstN(pool.Ref, needRef, themPay);
            TakeFirstN(pool.Rec, needRec, themPay);
            TakeFirstN(pool.Scrap, needScrap, themPay);

            if (themPay.Count < needRef + needRec + needScrap)
                return (false, null, "partner lacks exact metal to cover " + priceRef.ToString("0.00") + " ref");

            // 3) Send the offer: we GIVE item; they GIVE metal
            return await SendTradeOfferAsync(
                partnerSteamId64,
                meGive: ourAssets,
                themGive: themPay,
                message: $"Selling '{itemName}' for {priceRef:0.00} ref",
                accessToken: null);
        }




        // ---------------- Inventory helpers (Inv* DTOs) ----------------

        private sealed class InvResponse
        {
            public List<InvAsset>? assets { get; set; }
            public List<InvDesc>? descriptions { get; set; }
        }
        private sealed class InvAsset
        {
            public string assetid { get; set; } = string.Empty;
            public string classid { get; set; } = string.Empty;
            public string instanceid { get; set; } = string.Empty;
        }
        private sealed class InvDesc
        {
            public string classid { get; set; } = string.Empty;
            public string instanceid { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
            public string market_hash_name { get; set; } = string.Empty;
        }

        private async Task<InvResponse?> FetchCommunityInventoryAsync()
        {
            string url = "https://steamcommunity.com/profiles/" + _botSteamId64 + "/inventory/440/2?l=english&count=5000";
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                using HttpResponseMessage resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { Console.WriteLine("[Inv] HTTP " + (int)resp.StatusCode + " at " + url); return null; }

                string text = await resp.Content.ReadAsStringAsync();
                if (text.Length > 0 && text[0] == '<') { Console.WriteLine("[Inv] Non-JSON inventory payload (HTML) from " + url); return null; }

                return JsonConvert.DeserializeObject<InvResponse>(text);
            }
            catch (Exception ex) { Console.WriteLine("[Inv] Error " + ex.Message + " at " + url); return null; }
        }

        private async Task<InvResponse?> FetchInventoryForUserAsync(string steamId64)
        {
            string url = "https://steamcommunity.com/inventory/" + steamId64 + "/440/2?l=english&count=5000";
            try
            {
                System.Net.Http.HttpResponseMessage resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || (json.Length > 0 && json[0] == '<'))
                    return null;

                InvResponse? inv = Newtonsoft.Json.JsonConvert.DeserializeObject<InvResponse>(json);
                return inv;
            }
            catch
            {
                return null;
            }
        }

        private async Task<InvResponse?> FetchPartnerInventoryAsync(string partnerSteamId64)
        {
            string url = "https://steamcommunity.com/inventory/" + partnerSteamId64 + "/440/2?l=english&count=5000";
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://steamcommunity.com/profiles/" + partnerSteamId64 + "/inventory");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                HttpResponseMessage resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { Console.WriteLine("[Inv] partner inv HTTP " + ((int)resp.StatusCode)); return null; }

                string text = await resp.Content.ReadAsStringAsync();
                if (text.Length > 0 && text[0] == '<') { Console.WriteLine("[Inv] Non-JSON partner inventory payload (HTML)."); return null; }

                return JsonConvert.DeserializeObject<InvResponse>(text);
            }
            catch (Exception ex) { Console.WriteLine("[Inv] partner inv error: " + ex.Message); return null; }
        }

        private static string? FindAssetIdByName(InvResponse inv, string itemName)
        {
            if (inv.assets == null || inv.descriptions == null) return null;

            Dictionary<string, InvDesc> dx = new Dictionary<string, InvDesc>(StringComparer.Ordinal);
            foreach (InvDesc d in inv.descriptions) dx[d.classid + "_" + d.instanceid] = d;

            string target = StripLeadingThe(itemName);

            foreach (InvAsset a in inv.assets)
            {
                InvDesc d;
                if (!dx.TryGetValue(a.classid + "_" + a.instanceid, out d)) continue;
                string raw = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;
                string name = StripLeadingThe(raw);

                if (NameCmp.Equals(name, target))
                    return a.assetid;
            }
            return null;
        }

        private async Task<List<OfferMakeAsset>> FindOurItemAssetsAsync(string itemName, int quantity)
        {
            List<OfferMakeAsset> result = new List<OfferMakeAsset>();
            InvResponse? inv = await FetchCommunityInventoryAsync();
            if (inv == null || inv.assets == null || inv.descriptions == null) return result;

            System.Collections.Generic.Dictionary<string, InvDesc> descByKey =
                new System.Collections.Generic.Dictionary<string, InvDesc>(System.StringComparer.Ordinal);

            foreach (InvDesc d in inv.descriptions)
                descByKey[d.classid + "_" + d.instanceid] = d;

            string target = StripLeadingThe(itemName);

            foreach (InvAsset a in inv.assets)
            {
                InvDesc d;
                if (!descByKey.TryGetValue(a.classid + "_" + a.instanceid, out d)) continue;

                string raw = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;
                string canon = StripLeadingThe(raw);

                if (NameCmp.Equals(canon, target))
                {
                    result.Add(new OfferMakeAsset { assetid = a.assetid, amount = 1 });
                    if (result.Count >= quantity) break;
                }
            }

            return result;
        }




        // ---------------- Pure handling ----------------

        private sealed class PurePool
        {
            public List<string> Keys = new List<string>();
            public List<string> Ref = new List<string>();
            public List<string> Rec = new List<string>();
            public List<string> Scrap = new List<string>();
        }

        private static bool IsKey(string marketHashName) =>
            marketHashName?.IndexOf("Mann Co. Supply Crate Key", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsMetal(string marketHashName, out string kind)
        {
            kind = string.Empty;
            string s = marketHashName ?? string.Empty;
            if (s.IndexOf("Refined Metal", StringComparison.OrdinalIgnoreCase) >= 0) { kind = "ref"; return true; }
            if (s.IndexOf("Reclaimed Metal", StringComparison.OrdinalIgnoreCase) >= 0) { kind = "rec"; return true; }
            if (s.IndexOf("Scrap Metal", StringComparison.OrdinalIgnoreCase) >= 0) { kind = "scrap"; return true; }
            return false;
        }

        private PurePool CollectPure(InvResponse inv)
        {
            PurePool p = new PurePool();

            System.Collections.Generic.Dictionary<string, InvDesc> descByKey =
                new System.Collections.Generic.Dictionary<string, InvDesc>(System.StringComparer.Ordinal);

            foreach (InvDesc d in (inv.descriptions ?? new List<InvDesc>()))
                descByKey[d.classid + "_" + d.instanceid] = d;

            foreach (InvAsset a in (inv.assets ?? new List<InvAsset>()))
            {
                InvDesc d;
                if (!descByKey.TryGetValue(a.classid + "_" + a.instanceid, out d)) continue;

                string name = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;
                if (IsKey(name)) { p.Keys.Add(a.assetid); continue; }

                string kind;
                if (IsMetal(name, out kind))
                {
                    if (kind == "ref") p.Ref.Add(a.assetid);
                    else if (kind == "rec") p.Rec.Add(a.assetid);
                    else if (kind == "scrap") p.Scrap.Add(a.assetid);
                }
            }

            return p;
        }


        private sealed class PureSelection
        {
            public int Keys;
            public int Ref;
            public int Rec;
            public int Scrap;
            public decimal TotalRef;
        }

        private static PureSelection MakeChangeGreedy(decimal amountRef, decimal keySellRef)
        {
            PureSelection s = new PureSelection();
            if (keySellRef <= 0m) keySellRef = 56.00m;

            int keys = (int)Math.Floor(amountRef / keySellRef);
            amountRef -= keys * keySellRef;

            int refi = (int)Math.Floor(amountRef);
            amountRef -= refi;

            int rec = (int)Math.Floor(amountRef / 0.33m);
            amountRef -= rec * 0.33m;

            int scrap = (int)Math.Round(amountRef / 0.11m, MidpointRounding.AwayFromZero);
            amountRef -= scrap * 0.11m;

            s.Keys = keys; s.Ref = refi; s.Rec = rec; s.Scrap = scrap;
            s.TotalRef = keys * keySellRef + refi + rec * 0.33m + scrap * 0.11m;
            return s;
        }

        private static (int Ref, int Rec, int Scrap) BreakRef(decimal amountRef)
        {
            int scrapTotal = (int)Math.Round(amountRef * 9m, MidpointRounding.AwayFromZero);
            int r = scrapTotal / 9; scrapTotal %= 9;
            int rc = scrapTotal / 3; scrapTotal %= 3;
            int s = scrapTotal;
            return (r, rc, s);
        }

        private async Task<List<OfferMakeAsset>?> PickOurMetalAsync(int needRef, int needRec, int needScrap)
        {
            InvResponse? inv = await FetchCommunityInventoryAsync();
            if (inv == null || inv.assets == null || inv.descriptions == null) return null;

            Dictionary<string, InvDesc> dx = new Dictionary<string, InvDesc>(StringComparer.Ordinal);
            foreach (InvDesc d in inv.descriptions) dx[d.classid + "_" + d.instanceid] = d;

            List<OfferMakeAsset> result = new List<OfferMakeAsset>();
            int gotRef = 0, gotRec = 0, gotScr = 0;

            foreach (InvAsset a in inv.assets)
            {
                InvDesc d;
                if (!dx.TryGetValue(a.classid + "_" + a.instanceid, out d)) continue;
                string n = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;

                if (gotRef < needRef && NameCmp.Equals(n, "Refined Metal")) { result.Add(new OfferMakeAsset { assetid = a.assetid }); gotRef++; continue; }
                if (gotRec < needRec && NameCmp.Equals(n, "Reclaimed Metal")) { result.Add(new OfferMakeAsset { assetid = a.assetid }); gotRec++; continue; }
                if (gotScr < needScrap && NameCmp.Equals(n, "Scrap Metal")) { result.Add(new OfferMakeAsset { assetid = a.assetid }); gotScr++; continue; }

                if (gotRef == needRef && gotRec == needRec && gotScr == needScrap) break;
            }

            if (gotRef < needRef || gotRec < needRec || gotScr < needScrap) return null;
            return result;
        }

        private static void TakeFirstN(List<string> from, int n, List<OfferMakeAsset> into)
        {
            for (int i = 0; i < n && i < from.Count; i++) into.Add(new OfferMakeAsset { assetid = from[i] });
        }

        // ---------------- Policy helpers ----------------

        private static readonly StringComparer NameCmp = StringComparer.OrdinalIgnoreCase;

        private static bool IsPureName(string name) =>
            NameCmp.Equals(name, "Refined Metal") ||
            NameCmp.Equals(name, "Reclaimed Metal") ||
            NameCmp.Equals(name, "Scrap Metal") ||
            NameCmp.Equals(name, "Mann Co. Supply Crate Key");

        private static bool CheckBasicPolicy(OfferSummary summary, ConfigRoot cfg, out string reason)
        {
            HashSet<string> buyAllowed = new HashSet<string>(NameCmp);
            foreach (BuyConfig b in cfg.BuyConfig)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Name)) continue;
                string raw = b.Name.Trim();
                buyAllowed.Add(raw);
                buyAllowed.Add(StripLeadingThe(raw));
            }

            HashSet<string> sellAllowed = new HashSet<string>(NameCmp);
            foreach (SellConfig s in cfg.SellConfig)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                string raw = s.Name.Trim();
                sellAllowed.Add(raw);
                sellAllowed.Add(StripLeadingThe(raw));
            }

            foreach (var kv in summary.ItemsToReceiveByName)
            {
                string name = kv.Key;
                if (!IsPureName(name) && !buyAllowed.Contains(name))
                {
                    reason = "not a tracked buy item: " + name;
                    return false;
                }
            }

            foreach (var kv in summary.ItemsToGiveByName)
            {
                string name = kv.Key;
                if (!IsPureName(name) && !sellAllowed.Contains(name))
                {
                    reason = "not a tracked sell item: " + name;
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private decimal GetKeyRefPrice()
        {
            PriceResult key;
            if (_priceStore.TryGetPrice("mann co. supply crate key", out key) && key.MostCommonSellPrice > 0m)
                return key.MostCommonSellPrice;
            return 56.00m;
        }

        private static decimal RefFromPure(Dictionary<string, int> byName, decimal keyRefPrice)
        {
            int keys = 0, refined = 0, reclaimed = 0, scrap = 0;

            foreach (var kv in byName)
            {
                string n = kv.Key;
                int c = kv.Value;
                if (NameCmp.Equals(n, "Mann Co. Supply Crate Key")) keys += c;
                else if (NameCmp.Equals(n, "Refined Metal")) refined += c;
                else if (NameCmp.Equals(n, "Reclaimed Metal")) reclaimed += c;
                else if (NameCmp.Equals(n, "Scrap Metal")) scrap += c;
            }

            decimal refFromMetal = refined + (reclaimed * 0.33m) + (scrap * 0.11m);
            decimal refFromKeys = keys * keyRefPrice;
            return refFromMetal + refFromKeys;
        }

        private static bool OnlyPure(Dictionary<string, int> byName)
        {
            foreach (string n in byName.Keys) if (!IsPureName(n)) return false;
            return true;
        }

        private static void CountPureToGive(OfferSummary s, out int keys, out int refined, out int reclaimed, out int scrap)
        {
            keys = 0; refined = 0; reclaimed = 0; scrap = 0;

            foreach (var kv in s.ItemsToGiveByName)
            {
                if (NameCmp.Equals(kv.Key, "Mann Co. Supply Crate Key")) keys += kv.Value;
                else if (NameCmp.Equals(kv.Key, "Refined Metal")) refined += kv.Value;
                else if (NameCmp.Equals(kv.Key, "Reclaimed Metal")) reclaimed += kv.Value;
                else if (NameCmp.Equals(kv.Key, "Scrap Metal")) scrap += kv.Value;
            }
        }

        private async Task<(bool Ok, string Reason)> CheckPureBalanceAsync(OfferSummary summary)
        {
            CountPureToGive(summary, out int needKeys, out int needRef, out int needRec, out int needScrap);

            if (needKeys == 0 && needRef == 0 && needRec == 0 && needScrap == 0)
                return (true, string.Empty);

            PureSnapshot inv = await GetPureSnapshotAsync();

            if (needKeys > inv.Keys) return (false, "insufficient keys: need " + needKeys + ", have " + inv.Keys);
            if (needRef > inv.Refined) return (false, "insufficient refined: need " + needRef + ", have " + inv.Refined);
            if (needRec > inv.Reclaimed) return (false, "insufficient reclaimed: need " + needRec + ", have " + inv.Reclaimed);
            if (needScrap > inv.Scrap) return (false, "insufficient scrap: need " + needScrap + ", have " + inv.Scrap);

            return (true, string.Empty);
        }

        private async Task<Dictionary<string, int>> GetCountsForNamesAsync(IEnumerable<string> names)
        {
            HashSet<string> targets = new HashSet<string>(NameCmp);
            foreach (string nm in names)
            {
                string stripped = StripLeadingThe(nm);
                targets.Add(nm);
                targets.Add(stripped);
                targets.Add("The " + stripped);
            }

            Dictionary<string, int> result = new Dictionary<string, int>(NameCmp);
            InvResponse? inv = await FetchCommunityInventoryAsync();
            if (inv == null) return result;

            Dictionary<string, InvDesc> dx = new Dictionary<string, InvDesc>(StringComparer.Ordinal);
            foreach (InvDesc d in inv.descriptions ?? new List<InvDesc>())
                dx[d.classid + "_" + d.instanceid] = d;

            foreach (InvAsset a in inv.assets ?? new List<InvAsset>())
            {
                InvDesc d;
                if (!dx.TryGetValue(a.classid + "_" + a.instanceid, out d)) continue;
                string n = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;
                string ns = StripLeadingThe(n);

                if (targets.Contains(n) || targets.Contains(ns))
                {
                    if (!result.ContainsKey(n)) result[n] = 0;
                    result[n]++;
                }
            }

            return result;
        }

        private async Task<(bool Ok, string Reason)> CheckSellAssetsAvailableAsync(List<Asset>? itemsToGive)
        {
            if (itemsToGive == null || itemsToGive.Count == 0) return (true, string.Empty);

            HashSet<string> have = await GetLiveAssetIdsAsync();
            if (have.Count == 0) return (false, "inventory unavailable (private/rate-limited)");

            foreach (Asset a in itemsToGive)
            {
                if (a.appid != 440) continue;
                if (string.IsNullOrEmpty(a.assetid)) return (false, "missing asset id in offer");
                if (!have.Contains(a.assetid)) return (false, "we no longer own asset " + a.assetid);
            }
            return (true, string.Empty);
        }

        private async Task<HashSet<string>> GetLiveAssetIdsAsync()
        {
            HashSet<string>? api = await TryGetAssetIdsViaWebApiAsync();
            if (api != null && api.Count > 0) return api;

            InvResponse? inv = await FetchCommunityInventoryAsync();
            if (inv?.assets == null) return new HashSet<string>(StringComparer.Ordinal);

            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (InvAsset a in inv.assets) if (!string.IsNullOrEmpty(a.assetid)) ids.Add(a.assetid);
            return ids;
        }

        // ---------------- Low-level helpers ----------------

        public async Task<bool> VerifyCommunityCookiesOnceAsync()
        {
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://steamcommunity.com/tradeoffers/");
                req.Headers.Referrer = new Uri("https://steamcommunity.com/tradeoffers/");
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                HttpResponseMessage resp = await _community.SendAsync(req);
                int code = (int)resp.StatusCode;

                if (code == 200) { Console.WriteLine("[Trade] Community cookies look valid (200 at /tradeoffers/)."); return true; }
                if (code == 302 || code == 401 || code == 403)
                {
                    Console.WriteLine("[Trade] Community cookie check HTTP " + code.ToString(CultureInfo.InvariantCulture) +
                                      " → please refresh STEAM_SESSIONID and STEAM_LOGIN_SECURE for the bot account.");
                    return false;
                }

                Console.WriteLine("[Trade] Community cookie check got HTTP " + code.ToString(CultureInfo.InvariantCulture) + ".");
                return code == 200;
            }
            catch (Exception ex) { Console.WriteLine("[Trade] Community cookie check error: " + ex.Message); return false; }
        }

        private string? GetCookieValue(string baseUrl, string name)
        {
            Uri uri = new Uri(baseUrl);
            foreach (Cookie c in _cookieJar.GetCookies(uri))
                if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c.Value;
            return null;
        }

        private static string TrimForLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        private void LoadCommunityCookiesFromEnvDual()
        {
            string sessionId = Environment.GetEnvironmentVariable("STEAM_SESSIONID") ?? string.Empty;
            string steamLoginSecure = Environment.GetEnvironmentVariable("STEAM_LOGIN_SECURE") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(steamLoginSecure))
            {
                Console.WriteLine("[Trade] Env cookies missing (STEAM_SESSIONID / STEAM_LOGIN_SECURE).");
                return;
            }

            AddCookie("https://steamcommunity.com/", "steamcommunity.com", "sessionid", sessionId, false);
            AddCookie("https://steamcommunity.com/", ".steamcommunity.com", "sessionid", sessionId, false);
            AddCookie("https://steamcommunity.com/", "steamcommunity.com", "steamLoginSecure", steamLoginSecure, true);
            AddCookie("https://steamcommunity.com/", ".steamcommunity.com", "steamLoginSecure", steamLoginSecure, true);
        }

        private void AddCookie(string baseUrl, string domain, string name, string value, bool httpOnly)
        {
            try
            {
                Uri baseUri = new Uri(baseUrl);
                Cookie c = new Cookie
                {
                    Name = name,
                    Value = value.Trim(),
                    Domain = domain,
                    Path = "/",
                    HttpOnly = httpOnly,
                    Secure = true
                };
                _cookieJar.Add(baseUri, c);
            }
            catch (Exception ex) { Console.WriteLine("[Trade] AddCookie(" + name + ") failed: " + ex.Message); }
        }

        private async Task<PureSnapshot> GetPureSnapshotAsync()
        {
            if (_pureCache != null && (DateTime.UtcNow - _pureCacheUtc) < TimeSpan.FromSeconds(60))
                return _pureCache;

            PureSnapshot snap = new PureSnapshot();

            InvResponse? inv = await FetchCommunityInventoryAsync();
            if (inv == null)
            {
                Console.WriteLine("[Inv] inventory unavailable; assuming zero pure.");
                _pureCache = snap; _pureCacheUtc = DateTime.UtcNow; return snap;
            }

            Dictionary<string, InvDesc> dx = new Dictionary<string, InvDesc>(StringComparer.Ordinal);
            foreach (InvDesc d in inv.descriptions ?? new List<InvDesc>())
                dx[d.classid + "_" + d.instanceid] = d;

            foreach (InvAsset a in inv.assets ?? new List<InvAsset>())
            {
                InvDesc d;
                if (!dx.TryGetValue(a.classid + "_" + a.instanceid, out d)) continue;
                string n = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;

                if (NameCmp.Equals(n, "Mann Co. Supply Crate Key")) snap.Keys++;
                else if (NameCmp.Equals(n, "Refined Metal")) snap.Refined++;
                else if (NameCmp.Equals(n, "Reclaimed Metal")) snap.Reclaimed++;
                else if (NameCmp.Equals(n, "Scrap Metal")) snap.Scrap++;
            }

            _pureCache = snap; _pureCacheUtc = DateTime.UtcNow;
            Console.WriteLine($"[Inv] Keys={snap.Keys} Ref={snap.Refined} Rec={snap.Reclaimed} Scr={snap.Scrap}");
            return snap;
        }

        // ---------------- DTOs for GetTradeOffers ----------------

        private sealed class GetTradeOffersResponse { public InnerResponse? response { get; set; } }
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

        private sealed class EconItemsResponse { public EconResult? result { get; set; } }
        private sealed class EconResult { public int status { get; set; } public List<EconItem>? items { get; set; } }
        private sealed class EconItem { public string id { get; set; } = ""; public int defindex { get; set; } public int quantity { get; set; } = 1; }

        private async Task<HashSet<string>?> TryGetAssetIdsViaWebApiAsync()
        {
            try
            {
                string econUrl = "https://api.steampowered.com/IEconItems_440/GetPlayerItems/v1/?" +
                                 "key=" + Uri.EscapeDataString(_apiKey) +
                                 "&steamid=" + Uri.EscapeDataString(_botSteamId64);

                using HttpResponseMessage resp = await _http.GetAsync(econUrl);
                if (!resp.IsSuccessStatusCode) return null;

                string body = await resp.Content.ReadAsStringAsync();
                EconItemsResponse? econ = JsonConvert.DeserializeObject<EconItemsResponse>(body);

                if (econ?.result?.status != 1 || econ.result.items == null)
                    return new HashSet<string>(StringComparer.Ordinal);

                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (EconItem it in econ.result.items)
                    if (!string.IsNullOrEmpty(it.id)) ids.Add(it.id);

                if (ids.Count > 0) Console.WriteLine("[Inv] Asset ids via Web API: " + ids.Count);
                return ids;
            }
            catch { return null; }
        }

        // ---------------- Summarization & utils ----------------

        private static string AccountIdToSteamId64(int accountId)
        {
            long id64 = 76561197960265728L + (long)accountId;
            return id64.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, Description> BuildDescriptionIndex(List<Description>? descriptions)
        {
            Dictionary<string, Description> map = new Dictionary<string, Description>();
            if (descriptions == null) return map;

            foreach (Description d in descriptions)
                map[d.classid + "_" + d.instanceid] = d;

            return map;
        }

        private static string CanonicalName(Description d)
        {
            if (!string.IsNullOrEmpty(d.market_hash_name)) return d.market_hash_name;
            if (!string.IsNullOrEmpty(d.name)) return d.name;
            return d.classid + "_" + d.instanceid;
        }

        private static OfferSummary SummarizeOffer(TradeOffer offer, Dictionary<string, Description> descIndex)
        {
            OfferSummary s = new OfferSummary();

            if (offer.items_to_receive != null)
            {
                foreach (Asset a in offer.items_to_receive)
                {
                    if (a.appid != 440) continue;
                    string dkey = a.classid + "_" + a.instanceid;
                    string rawName = descIndex.TryGetValue(dkey, out Description d) ? (string.IsNullOrEmpty(d.market_hash_name) ? d.name : d.market_hash_name) : dkey;
                    string name = StripLeadingThe(rawName);

                    int count = 1;
                    if (!string.IsNullOrEmpty(a.amount))
                    {
                        if (int.TryParse(a.amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                            count = parsed;
                    }

                    if (!s.ItemsToReceiveByName.ContainsKey(name)) s.ItemsToReceiveByName[name] = 0;
                    s.ItemsToReceiveByName[name] += count;
                }
            }

            if (offer.items_to_give != null)
            {
                foreach (Asset a in offer.items_to_give)
                {
                    if (a.appid != 440) continue;
                    string dkey = a.classid + "_" + a.instanceid;
                    string rawName = descIndex.TryGetValue(dkey, out Description d) ? (string.IsNullOrEmpty(d.market_hash_name) ? d.name : d.market_hash_name) : dkey;
                    string name = StripLeadingThe(rawName);

                    int count = 1;
                    if (!string.IsNullOrEmpty(a.amount))
                    {
                        if (int.TryParse(a.amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                            count = parsed;
                    }

                    if (!s.ItemsToGiveByName.ContainsKey(name)) s.ItemsToGiveByName[name] = 0;
                    s.ItemsToGiveByName[name] += count;
                }
            }

            return s;
        }

        private static string StripLeadingThe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            return s.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? s.Substring(4) : s;
        }
        private void DumpCommunityCookieDebug()
        {
            try
            {
                var uri = new Uri("https://steamcommunity.com/");
                var cookies = _cookieJar.GetCookies(uri);
                Console.WriteLine("[Trade] Cookies for steamcommunity.com:");
                foreach (Cookie c in cookies)
                {
                    string masked = c.Value.Length <= 6 ? c.Value : (c.Value.Substring(0, 3) + "…(" + c.Value.Length + ")");
                    Console.WriteLine($"    {c.Name} = {masked} | Domain={c.Domain} | Path={c.Path}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Trade] Cookie dump error: " + ex.Message);
            }
        }


        private static void LogOfferSummary(string offerId, string partnerSteamId64, OfferSummary s)
        {
            Console.WriteLine("[Trade] Offer " + offerId + " from " + partnerSteamId64);

            if (s.ItemsToReceiveByName.Count > 0)
            {
                Console.WriteLine("  We RECEIVE:");
                foreach (var kv in s.ItemsToReceiveByName)
                    Console.WriteLine("    + " + kv.Value.ToString(CultureInfo.InvariantCulture) + " × " + kv.Key);
            }

            if (s.ItemsToGiveByName.Count > 0)
            {
                Console.WriteLine("  We GIVE:");
                foreach (var kv in s.ItemsToGiveByName)
                    Console.WriteLine("    - " + kv.Value.ToString(CultureInfo.InvariantCulture) + " × " + kv.Key);
            }

            if (s.ItemsToReceiveByName.Count == 0 && s.ItemsToGiveByName.Count == 0)
                Console.WriteLine("  (no TF2 items in this offer)");
        }

        private sealed class PureSnapshot
        {
            public int Keys { get; set; }
            public int Refined { get; set; }
            public int Reclaimed { get; set; }
            public int Scrap { get; set; }
        }
    }
}
