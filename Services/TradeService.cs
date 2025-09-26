using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArkRoxBot.Models;
using ArkRoxBot.Models.Config;
using Newtonsoft.Json;
using SteamKit2.WebUI.Internal;

namespace ArkRoxBot.Services
{
    public sealed class TradeService : IDisposable
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
        private HttpClient? _communityHttp;
        private string? _sessionId;
        private readonly HttpClient _community;
        private readonly CookieContainer _cookieJar = new CookieContainer();

        private System.Threading.Timer? _timer;
        private System.Threading.CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _isPolling = 0;
        private const decimal AcceptToleranceRef = 0.02m; // was 0.01m
        private decimal _sellTolRef;


        // simple cache for inventory pure snapshot (reduce calls)
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

            // ---------- Cookie jar ----------
            _cookieJar = new System.Net.CookieContainer();

            // ---------- General API client (Steam Web API, backpack.tf, etc.) ----------
            System.Net.Http.HttpClientHandler apiHandler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _http = new HttpClient(apiHandler);
            _http.Timeout = TimeSpan.FromSeconds(30);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ArkRoxBot/1.0");

            // ---------- Community client (tradeoffer accept/decline) ----------
            System.Net.Http.HttpClientHandler communityHandler = new System.Net.Http.HttpClientHandler
            {
                CookieContainer = _cookieJar,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
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

            // ---------- Load cookies from env (to BOTH domains) + debug dump ----------
            LoadCommunityCookiesFromEnvDual();
            DumpCommunityCookieDebug();

            // ---------- Feature flags ----------
            _tradingEnabled = string.Equals(
                Environment.GetEnvironmentVariable("TRADING_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            _dryRun = !string.Equals(
                Environment.GetEnvironmentVariable("DRY_RUN"),
                "false",
                StringComparison.OrdinalIgnoreCase);

            _verifySellAssets = !string.Equals(
                Environment.GetEnvironmentVariable("VERIFY_SELL_ASSETS"),
                "false",
                StringComparison.OrdinalIgnoreCase);

            // ---------- SELL tolerance (parse -> assign) ----------
            string tolRaw = Environment.GetEnvironmentVariable("SELL_TOL_REF") ?? string.Empty;
            decimal parsedTol;
            if (decimal.TryParse(
                    tolRaw,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsedTol))
            {
                _sellTolRef = parsedTol;
            }
            else
            {
                _sellTolRef = AcceptToleranceRef;
            }

            Console.WriteLine("[Trade] Flags → TRADING_ENABLED=" + _tradingEnabled +
                              ", DRY_RUN=" + _dryRun +
                              ", VERIFY_SELL_ASSETS=" + _verifySellAssets +
                              ", SELL_TOL_REF=" + _sellTolRef.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }





        public void Start()
        {
            if (_timer != null) return;
            _cts = new System.Threading.CancellationTokenSource();

            _timer = new System.Threading.Timer(async _ =>
            {
                try { await PollAsync(_cts.Token); }
                catch (Exception ex) { Console.WriteLine("[Trade] Timer poll error: " + ex.Message); }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        private async Task PollAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            if (System.Threading.Interlocked.Exchange(ref _isPolling, 1) == 1)
                return;

            try
            {
                await PollOnceAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Trade] Poll error: " + ex.Message);
            }
            finally
            {
                System.Threading.Volatile.Write(ref _isPolling, 0);
            }
        }

        // Call this once after Start() to print a snapshot to the console.
        public Task LogStockSnapshotOnceAsync()
        {
            return LogStockSnapshotAsync();
        }

        private async Task LogStockSnapshotAsync()
        {
            try
            {
                // Pure snapshot
                PureSnapshot pure = await GetPureSnapshotAsync();
                Console.WriteLine("[Stock] Pure → Keys=" + pure.Keys + ", Ref=" + pure.Refined +
                                  ", Rec=" + pure.Reclaimed + ", Scrap=" + pure.Scrap);

                // Tracked sell items snapshot
                ConfigRoot cfg = _configLoader.LoadItems();

                // normalize targets ("The Team Captain" → "Team Captain")
                HashSet<string> names = new HashSet<string>(
                    cfg.SellConfig.Select(s => StripLeadingThe(s.Name)),
                    NameCmp
                );

                Dictionary<string, int> have = await GetCountsForNamesAsync(names);
                foreach (KeyValuePair<string, int> kv in have.OrderBy(k => k.Key))
                    Console.WriteLine("[Stock] " + kv.Key + ": " + kv.Value);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Stock] Snapshot failed: " + ex.Message);
            }
        }

        private static async Task<string> SafeReadSnippetAsync(HttpResponseMessage resp)
        {
            try
            {
                string body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(body))
                {
                    return "(empty)";
                }
                int len = Math.Min(280, body.Length);
                return body.Substring(0, len).Replace("\r", " ").Replace("\n", " ");
            }
            catch
            {
                return "(no body)";
            }
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

            // Add cookies for both steamcommunity.com and .steamcommunity.com
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
                System.Net.Cookie c = new System.Net.Cookie
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
            catch (Exception ex)
            {
                Console.WriteLine("[Trade] AddCookie(" + name + ") failed: " + ex.Message);
            }
        }


        private void DumpCommunityCookieDebug()
        {
            try
            {
                Uri uri = new Uri("https://steamcommunity.com/");
                System.Net.CookieCollection cookies = _cookieJar.GetCookies(uri);

                Console.WriteLine("[Trade] Cookies for steamcommunity.com:");
                foreach (System.Net.Cookie c in cookies)
                {
                    string valueMasked = c.Value.Length <= 6 ? c.Value : (c.Value.Substring(0, 3) + "…(" + c.Value.Length.ToString() + ")");
                    Console.WriteLine("    " + c.Name + " = " + valueMasked + " | Domain=" + c.Domain + " | Path=" + c.Path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Trade] Cookie dump error: " + ex.Message);
            }
        }


        public void SetCommunityCookies(string sessionId, string steamLoginSecure)
        {
            var baseUri = new Uri("https://steamcommunity.com/");
            _cookieJar.Add(baseUri, new Cookie("sessionid", sessionId) { HttpOnly = false });
            _cookieJar.Add(baseUri, new Cookie("steamLoginSecure", steamLoginSecure) { HttpOnly = true });
        }


        private async Task<string?> GetInventoryJsonAsync()
        {
            string url = "https://steamcommunity.com/inventory/" + _botSteamId64 + "/440/2?l=english&count=5000";

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                    HttpResponseMessage resp = await _http.SendAsync(req);

                    if (resp.IsSuccessStatusCode)
                    {
                        string text = await resp.Content.ReadAsStringAsync();
                        return text;
                    }

                    int code = (int)resp.StatusCode;

                    // transient errors – back off and retry
                    if (code == 400 || code == 429 || code >= 500)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
                        continue;
                    }

                    Console.WriteLine("[Inv] HTTP " + code.ToString(CultureInfo.InvariantCulture) + " fetching inventory.");
                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Inv] Fetch error (try " + attempt.ToString(CultureInfo.InvariantCulture) + "): " + ex.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
                }
            }

            return null;
        }



        private static string Canon(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            return StripLeadingThe(s).Trim();
        }

        private void EnsureCommunityHttp()
        {
            if (_communityHttp != null) return;

            _sessionId = Environment.GetEnvironmentVariable("STEAM_SESSIONID");
            string? steamLoginSecure = Environment.GetEnvironmentVariable("STEAM_LOGIN_SECURE");

            if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(steamLoginSecure))
                throw new InvalidOperationException("Missing STEAM_SESSIONID or STEAM_LOGIN_SECURE env vars.");

            var handler = new HttpClientHandler { CookieContainer = _cookieJar, AutomaticDecompression = System.Net.DecompressionMethods.All };

            // Set cookies for steamcommunity.com
            var domain = ".steamcommunity.com";
            _cookieJar.Add(new Uri("https://steamcommunity.com/"), new Cookie("sessionid", _sessionId) { Domain = domain, Path = "/" });
            _cookieJar.Add(new Uri("https://steamcommunity.com/"), new Cookie("steamLoginSecure", steamLoginSecure) { Domain = domain, Path = "/" });

            _communityHttp = new HttpClient(handler);
            _communityHttp.DefaultRequestHeaders.Referrer = new Uri("https://steamcommunity.com/tradeoffers/");
            _communityHttp.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
            _communityHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 ArkRoxBot/1.0");
        }

        private static readonly int[] CommunityTransient = { 429, 500, 502, 503, 504 };

        private async Task<bool> SendCommunityPostWithRetriesAsync(
            string url,
            Dictionary<string, string> form,
            string referer,
            string actionLabel,
            string offerId)
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
                        Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " HTTP " + code.ToString() + " → cookies invalid?");
                        return false;
                    }

                    if (isTransient && attempt < 4)
                    {
                        int delayMs = 400 * attempt;
                        Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " transient " + code.ToString() +
                                          " → retry in " + delayMs.ToString() + " ms.");
                        await Task.Delay(delayMs);
                        continue;
                    }

                    Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " failed HTTP " + code.ToString() + " → " + body);
                    return false;
                }
                catch (Exception ex)
                {
                    if (attempt < 4)
                    {
                        int delayMs = 400 * attempt;
                        Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " error: " + ex.Message +
                                          " → retry in " + delayMs.ToString() + " ms.");
                        await Task.Delay(delayMs);
                        continue;
                    }
                    Console.WriteLine("[Trade] " + actionLabel + " " + offerId + " error: " + ex.Message + " (giving up)");
                    return false;
                }
            }

            return false;
        }


        public async Task<bool> VerifyCommunityCookiesOnceAsync()
        {
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://steamcommunity.com/tradeoffers/");
                req.Headers.Referrer = new Uri("https://steamcommunity.com/tradeoffers/");
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                HttpResponseMessage resp = await _community.SendAsync(req);
                int code = (int)resp.StatusCode;

                if (code == 200)
                {
                    Console.WriteLine("[Trade] Community cookies look valid (200 at /tradeoffers/).");
                    return true;
                }

                if (code == 302 || code == 401 || code == 403)
                {
                    Console.WriteLine("[Trade] Community cookie check HTTP " + code.ToString(CultureInfo.InvariantCulture) +
                                      " → please refresh STEAM_SESSIONID and STEAM_LOGIN_SECURE for the bot account.");
                    return false;
                }

                Console.WriteLine("[Trade] Community cookie check got HTTP " + code.ToString(CultureInfo.InvariantCulture) + ".");
                return code == 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Trade] Community cookie check error: " + ex.Message);
                return false;
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
                return;

            ConfigRoot cfg = _configLoader.LoadItems();

            foreach (TradeOffer offer in parsed.response.trade_offers_received)
            {
                // 2 = Active, 9 = Confirmation Needed (skip actions on 9 for now)
                if (offer.trade_offer_state != 2 && offer.trade_offer_state != 9)
                    continue;

                string partner64 = AccountIdToSteamId64(offer.accountid_other);
                OfferSummary summary = SummarizeOffer(offer, descIndex);

                LogOfferSummary(offer.tradeofferid, partner64, summary);

                if (offer.trade_offer_state == 9)
                {
                    Console.WriteLine("[Trade] Offer " + offer.tradeofferid + " needs confirmation → skipping for now.");
                    continue;
                }

                // ---- Basic policy (tracked items, etc.)
                string policyReason;
                bool policyOk = CheckBasicPolicy(summary, cfg, out policyReason);
                if (!policyOk)
                {
                    Console.WriteLine("[Policy] Decline: " + policyReason);
                    if (!_tradingEnabled || _dryRun)
                    {
                        Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + policyReason + ")");
                    }
                    else
                    {
                        try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                        catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                    }
                    continue;
                }

                // ---- Decide direction
                bool givesNonPure = summary.ItemsToGiveByName.Keys.Any(n => !IsPureName(n));
                bool receivesOnlyPure = OnlyPure(summary.ItemsToReceiveByName);

                bool receivesNonPure = summary.ItemsToReceiveByName.Keys.Any(n => !IsPureName(n));
                bool givesOnlyPure = OnlyPure(summary.ItemsToGiveByName);

                // ==========================
                // SELL: we GIVE item(s), partner pays PURE
                // ==========================
                if (givesNonPure && receivesOnlyPure)
                {
                    // Optional: ensure we still own the exact assets we are giving
                    if (_verifySellAssets)
                    {
                        (bool Ok, string Reason) sellAssetsCheck = await CheckSellAssetsAvailableAsync(offer.items_to_give);
                        if (!sellAssetsCheck.Ok)
                        {
                            Console.WriteLine("[Policy] Decline (sell assets): " + sellAssetsCheck.Reason);
                            if (!_tradingEnabled || _dryRun)
                            {
                                Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (sell assets)");
                            }
                            else
                            {
                                try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                                catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                            }
                            continue;
                        }
                    }

                    // Required ref based on SELL price * qty
                    decimal requiredRef = 0m;
                    foreach (KeyValuePair<string, int> kv in summary.ItemsToGiveByName)
                    {
                        string name = kv.Key;
                        int quantity = kv.Value;
                        if (IsPureName(name)) continue;

                        PriceResult price;
                        if (!_priceStore.TryGetPrice(name, out price) || price.MostCommonSellPrice <= 0m)
                        {
                            Console.WriteLine("[Policy] Decline: missing SELL price for " + name);
                            if (!_tradingEnabled || _dryRun)
                                Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (missing SELL price)");
                            else
                                try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                                catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                            goto NextOffer;
                        }

                        requiredRef += price.MostCommonSellPrice * quantity;
                    }

                    // Offered pure (keys→ref + metal)
                    decimal offeredRef = RefFromPure(summary.ItemsToReceiveByName, GetKeyRefPrice());

                    if (offeredRef + AcceptToleranceRef >= requiredRef)
                    {
                        Console.WriteLine("[Eval] SELL ok | offered=" + offeredRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                          " ref | required=" + requiredRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref");

                        if (!_tradingEnabled || _dryRun)
                        {
                            Console.WriteLine("[Trade] DRY-RUN: would Accept offer " + offer.tradeofferid + " (sell)");
                        }
                        else
                        {
                            try { await AcceptOfferCommunityAsync(offer.tradeofferid, partner64); }
                            catch (Exception ex) { Console.WriteLine("[Trade] Accept error: " + ex.Message); }
                        }
                    }
                    else
                    {
                        Console.WriteLine("[Policy] Decline: offered " + offeredRef.ToString("0.00", CultureInfo.InvariantCulture) +
                                          " ref < required SELL " + requiredRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref");
                        if (!_tradingEnabled || _dryRun)
                            Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (underpay)");
                        else
                            try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                            catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                    }

                    goto NextOffer;
                }

                // ==========================
                // BUY: we RECEIVE non-pure, we GIVE PURE
                // ==========================
                if (receivesNonPure && givesOnlyPure)
                {
                    // Stock caps
                    (bool Ok, string Reason) capCheck = await CheckStockCapsAsync(summary, cfg);
                    if (!capCheck.Ok)
                    {
                        Console.WriteLine("[Policy] Decline (stock cap): " + capCheck.Reason);
                        if (!_tradingEnabled || _dryRun)
                        {
                            Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + capCheck.Reason + ")");
                        }
                        else
                        {
                            try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                            catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                        }
                        goto NextOffer;
                    }

                    // Ensure we have pure to give
                    (bool Ok, string Reason) pureCheck = await CheckPureBalanceAsync(summary);
                    if (!pureCheck.Ok)
                    {
                        Console.WriteLine("[Policy] Decline (pure): " + pureCheck.Reason);
                        if (!_tradingEnabled || _dryRun)
                        {
                            Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + pureCheck.Reason + ")");
                        }
                        else
                        {
                            try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                            catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                        }
                        goto NextOffer;
                    }

                    // Evaluator decides
                    OfferEvaluationResult ev = _evaluator.Evaluate(summary);
                    Console.WriteLine(
                        "[Eval] " + ev.Decision.ToString() +
                        " | recv=" + ev.ReceiveRef.ToString("0.00", CultureInfo.InvariantCulture) +
                        " | give=" + ev.GiveRef.ToString("0.00", CultureInfo.InvariantCulture) +
                        " | profit=" + ev.ProfitRef.ToString("0.00", CultureInfo.InvariantCulture) +
                        " | reason=" + ev.Reason
                    );

                    if (!_tradingEnabled || _dryRun)
                    {
                        Console.WriteLine("[Trade] DRY-RUN: would " + ev.Decision.ToString() + " offer " + offer.tradeofferid + " (" + ev.Reason + ")");
                    }
                    else
                    {
                        try
                        {
                            if (ev.Decision == OfferDecision.Accept)
                                await AcceptOfferCommunityAsync(offer.tradeofferid, partner64);
                            else
                                await DeclineOfferCommunityAsync(offer.tradeofferid);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[Trade] Action error for " + offer.tradeofferid + ": " + ex.Message);
                        }
                    }

                    goto NextOffer;
                }

                // Mixed or unsupported (both sides contain non-pure, etc.)
                {
                    string why = "unsupported composition (mixed non-pure on both sides)";
                    Console.WriteLine("[Policy] Decline: " + why);
                    if (!_tradingEnabled || _dryRun)
                    {
                        Console.WriteLine("[Trade] DRY-RUN: would Decline offer " + offer.tradeofferid + " (" + why + ")");
                    }
                    else
                    {
                        try { await DeclineOfferCommunityAsync(offer.tradeofferid); }
                        catch (Exception ex) { Console.WriteLine("[Trade] Decline error: " + ex.Message); }
                    }
                }

            NextOffer:
                continue;
            }
        }


        private async Task<HashSet<string>> GetLiveAssetIdsAsync()
        {
            var api = await TryGetAssetIdsViaWebApiAsync();
            if (api != null && api.Count > 0) return api;

            var inv = await FetchCommunityInventoryAsync();
            if (inv?.assets == null) return new HashSet<string>(StringComparer.Ordinal);

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in inv.assets)
                if (!string.IsNullOrEmpty(a.assetid))
                    ids.Add(a.assetid);

            return ids;
        }


        private async Task<(bool Ok, string Reason)> CheckSellAssetsAvailableAsync(List<Asset>? itemsToGive)
        {
            if (itemsToGive == null || itemsToGive.Count == 0)
                return (true, string.Empty);

            HashSet<string> have = await GetLiveAssetIdsAsync();
            if (have.Count == 0)
                return (false, "inventory unavailable (private/rate-limited)");

            foreach (Asset a in itemsToGive)
            {
                if (a.appid != 440) continue;
                if (string.IsNullOrEmpty(a.assetid))
                    return (false, "missing asset id in offer");
                if (!have.Contains(a.assetid))
                    return (false, "we no longer own asset " + a.assetid);
            }
            return (true, string.Empty);
        }



        // ---------- Policy helpers ----------

        private static readonly StringComparer NameCmp = StringComparer.OrdinalIgnoreCase;

        private static bool IsPureName(string name)
        {
            return
                NameCmp.Equals(name, "Refined Metal") ||
                NameCmp.Equals(name, "Reclaimed Metal") ||
                NameCmp.Equals(name, "Scrap Metal") ||
                NameCmp.Equals(name, "Mann Co. Supply Crate Key");
        }

        // TradeService.cs  (replace the whole method with this)
        private static bool CheckBasicPolicy(OfferSummary summary, ConfigRoot cfg, out string reason)
        {
            // Build allowed-name sets from config, with both raw and normalized forms
            HashSet<string> buyAllowed = new HashSet<string>(NameCmp);
            foreach (BuyConfig b in cfg.BuyConfig)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Name)) continue;
                string raw = b.Name.Trim();
                buyAllowed.Add(raw);
                buyAllowed.Add(StripLeadingThe(raw)); // normalized
            }

            HashSet<string> sellAllowed = new HashSet<string>(NameCmp);
            foreach (SellConfig s in cfg.SellConfig)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                string raw = s.Name.Trim();
                sellAllowed.Add(raw);
                sellAllowed.Add(StripLeadingThe(raw)); // normalized
            }

            // If we RECEIVE non-pure (we are buying), it must be in BuyConfig
            foreach (KeyValuePair<string, int> kv in summary.ItemsToReceiveByName)
            {
                string name = kv.Key;
                if (!IsPureName(name) && !buyAllowed.Contains(name))
                {
                    reason = "not a tracked buy item: " + name;
                    return false;
                }
            }

            // If we GIVE non-pure (we are selling), it must be in SellConfig
            foreach (KeyValuePair<string, int> kv in summary.ItemsToGiveByName)
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

            // Very safe fallback if keys haven’t been priced yet
            return 56.00m;
        }

        private static decimal RefFromPure(Dictionary<string, int> byName, decimal keyRefPrice)
        {
            int keys = 0;
            int refined = 0;
            int reclaimed = 0;
            int scrap = 0;

            foreach (KeyValuePair<string, int> kv in byName)
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
            foreach (string n in byName.Keys)
                if (!IsPureName(n)) return false;
            return true;
        }

        // Count pure we GIVE in an offer
        private static void CountPureToGive(OfferSummary s, out int keys, out int refined, out int reclaimed, out int scrap)
        {
            keys = 0; refined = 0; reclaimed = 0; scrap = 0;

            foreach (KeyValuePair<string, int> kv in s.ItemsToGiveByName)
            {
                if (NameCmp.Equals(kv.Key, "Mann Co. Supply Crate Key")) keys += kv.Value;
                else if (NameCmp.Equals(kv.Key, "Refined Metal")) refined += kv.Value;
                else if (NameCmp.Equals(kv.Key, "Reclaimed Metal")) reclaimed += kv.Value;
                else if (NameCmp.Equals(kv.Key, "Scrap Metal")) scrap += kv.Value;
            }
        }
        // Ensure we have enough pure to GIVE (inventory snapshot via community inventory)
        private async Task<(bool Ok, string Reason)> CheckPureBalanceAsync(OfferSummary summary)
        {
            int needKeys; int needRef; int needRec; int needScrap;
            CountPureToGive(summary, out needKeys, out needRef, out needRec, out needScrap);

            if (needKeys == 0 && needRef == 0 && needRec == 0 && needScrap == 0)
            {
                return (true, string.Empty); // nothing to give
            }

            PureSnapshot inv = await GetPureSnapshotAsync();

            if (needKeys > inv.Keys)
                return (false, "insufficient keys: need " + needKeys + ", have " + inv.Keys);

            if (needRef > inv.Refined)
                return (false, "insufficient refined: need " + needRef + ", have " + inv.Refined);

            if (needRec > inv.Reclaimed)
                return (false, "insufficient reclaimed: need " + needRec + ", have " + inv.Reclaimed);

            if (needScrap > inv.Scrap)
                return (false, "insufficient scrap: need " + needScrap + ", have " + inv.Scrap);

            return (true, string.Empty);
        }

        // Count how many of the given item names we currently hold in our TF2 inventory.
        private async Task<Dictionary<string, int>> GetCountsForNamesAsync(IEnumerable<string> names)
        {
            // Build a target set with both exact and "no leading The"
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

            var dx = new Dictionary<string, InvDesc>(StringComparer.Ordinal);
            foreach (var d in inv.descriptions)
                dx[d.classid + "_" + d.instanceid] = d;

            foreach (var a in inv.assets)
            {
                if (!dx.TryGetValue(a.classid + "_" + a.instanceid, out var d)) continue;
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

        // Ensure accepting won't exceed BuyConfig.MaxQuantity for any tracked item we RECEIVE.
        private async Task<(bool Ok, string Reason)> CheckStockCapsAsync(OfferSummary summary, ConfigRoot cfg)
        {
            // Which non-pure items would we add?
            Dictionary<string, int> toReceive = new Dictionary<string, int>(NameCmp);
            foreach (KeyValuePair<string, int> kv in summary.ItemsToReceiveByName)
            {
                if (!IsPureName(kv.Key))
                    toReceive[kv.Key] = kv.Value;
            }

            if (toReceive.Count == 0)
                return (true, string.Empty);

            // Build quick lookup of configured max quantities
            Dictionary<string, int> maxByName = new Dictionary<string, int>(NameCmp);
            foreach (BuyConfig b in cfg.BuyConfig)
                maxByName[b.Name] = b.MaxQuantity;

            // Get our current counts for just these names
            Dictionary<string, int> current = await GetCountsForNamesAsync(toReceive.Keys);

            foreach (KeyValuePair<string, int> kv in toReceive)
            {
                string name = kv.Key;
                int need = kv.Value;

                // If the item isn't in BuyConfig, the basic policy should have blocked earlier. Skip here.
                int max;
                if (!maxByName.TryGetValue(name, out max) || max <= 0)
                    continue;

                int have = current.ContainsKey(name) ? current[name] : 0;

                if (have + need > max)
                {
                    string reason = "stock cap for " + name + ": have " + have.ToString(CultureInfo.InvariantCulture) +
                                    ", need " + need.ToString(CultureInfo.InvariantCulture) +
                                    ", max " + max.ToString(CultureInfo.InvariantCulture);
                    return (false, reason);
                }
            }

            return (true, string.Empty);
        }



        // ---------- HTTP actions ----------

        private sealed class AcceptTradeOfferEnvelope
        {
            public AcceptTradeOfferResponse response { get; set; }
        }
        private sealed class AcceptTradeOfferResponse
        {
            public string tradeofferid { get; set; } = string.Empty;

            // Steam sometimes returns one of these flags depending on account state
            public bool needs_mobile_confirmation { get; set; } = false;
            public bool needs_confirmation { get; set; } = false;

            // Some deployments include a success int; keep it for completeness
            public int success { get; set; } = 0;
        }

        private async Task<int> PreflightOfferPageAsync(string offerId)
        {
            string url = "https://steamcommunity.com/tradeoffer/" + offerId + "/";
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri(url);
                HttpResponseMessage resp = await _community.SendAsync(req);
                int code = (int)resp.StatusCode;
                string loc = resp.Headers.Location != null ? resp.Headers.Location.ToString() : string.Empty;

                if (code == 302 && loc.IndexOf("/market/eligibilitycheck", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("[Trade] Preflight → eligibilitycheck (OK to proceed).");
                    return code;
                }

                if (code != 200 && code != 204)
                {
                    Console.WriteLine("[Trade] Preflight offer page " + offerId + " HTTP " + code.ToString() +
                                      (string.IsNullOrEmpty(loc) ? "" : " → " + loc));
                }
                return code;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Trade] Preflight offer page error: " + ex.Message);
                return -1;
            }
        }


        private async Task<bool> AcceptOfferCommunityAsync(string offerId, string partnerSteamId64)
        {
            await PreflightOfferPageAsync(offerId); // keep

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



        private static readonly int[] TransientCodes = { 429, 500, 502, 503, 504 };

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



        // ---------- Summarization & utils ----------

        private static string AccountIdToSteamId64(int accountId)
        {
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

        // Prefer market_hash_name over name (Name Tags can't spoof market_hash_name)
        private static string CanonicalName(Description d)
        {
            if (!string.IsNullOrEmpty(d.market_hash_name))
                return d.market_hash_name;
            if (!string.IsNullOrEmpty(d.name))
                return d.name;
            return (d.classid + "_" + d.instanceid);
        }
        private async Task<HashSet<string>?> TryGetAssetIdsViaWebApiAsync()
        {
            try
            {
                string econUrl =
                    "https://api.steampowered.com/IEconItems_440/GetPlayerItems/v1/?" +
                    "key=" + Uri.EscapeDataString(_apiKey) +
                    "&steamid=" + Uri.EscapeDataString(_botSteamId64);

                using var resp = await _http.GetAsync(econUrl);
                if (!resp.IsSuccessStatusCode) return null;

                string body = await resp.Content.ReadAsStringAsync();
                var econ = JsonConvert.DeserializeObject<EconItemsResponse>(body);

                // Steam returns status==1 for success. Anything else → treat as empty.
                if (econ?.result?.status != 1 || econ.result.items == null)
                    return new HashSet<string>(StringComparer.Ordinal);

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var it in econ.result.items)
                {
                    // id can be large; keeping it as string is good
                    if (!string.IsNullOrEmpty(it.id))
                        ids.Add(it.id);
                }

                if (ids.Count > 0)
                    Console.WriteLine("[Inv] Asset ids via Web API: " + ids.Count);

                return ids;
            }
            catch
            {
                return null; // fall back to community inventory
            }
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
                    string rawName = dkey;
                    if (descIndex.TryGetValue(dkey, out Description d))
                    {
                        rawName = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;
                    }
                    string name = StripLeadingThe(rawName);

                    int count = 1;
                    if (!string.IsNullOrEmpty(a.amount))
                    {
                        int parsed;
                        if (int.TryParse(a.amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                            count = parsed;
                    }

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
                    string rawName = dkey;
                    if (descIndex.TryGetValue(dkey, out Description d))
                    {
                        rawName = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;
                    }
                    string name = StripLeadingThe(rawName);

                    int count = 1;
                    if (!string.IsNullOrEmpty(a.amount))
                    {
                        int parsed;
                        if (int.TryParse(a.amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                            count = parsed;
                    }

                    if (!s.ItemsToGiveByName.ContainsKey(name))
                        s.ItemsToGiveByName[name] = 0;

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
        private static void LogOfferSummary(string offerId, string partnerSteamId64, OfferSummary s)
        {
            Console.WriteLine("[Trade] Offer " + offerId + " from " + partnerSteamId64);

            if (s.ItemsToReceiveByName.Count > 0)
            {
                Console.WriteLine("  We RECEIVE:");
                foreach (KeyValuePair<string, int> kv in s.ItemsToReceiveByName)
                    Console.WriteLine("    + " + kv.Value.ToString(CultureInfo.InvariantCulture) + " × " + kv.Key);
            }

            if (s.ItemsToGiveByName.Count > 0)
            {
                Console.WriteLine("  We GIVE:");
                foreach (KeyValuePair<string, int> kv in s.ItemsToGiveByName)
                    Console.WriteLine("    - " + kv.Value.ToString(CultureInfo.InvariantCulture) + " × " + kv.Key);
            }

            if (s.ItemsToReceiveByName.Count == 0 && s.ItemsToGiveByName.Count == 0)
            {
                Console.WriteLine("  (no TF2 items in this offer)");
            }
        }

        private async Task<InvResponse?> FetchCommunityInventoryAsync()
        {
            string url = $"https://steamcommunity.com/profiles/{_botSteamId64}/inventory/440/2?l=english&count=5000";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine("[Inv] HTTP " + (int)resp.StatusCode + " at " + url);
                    return null;
                }

                string text = await resp.Content.ReadAsStringAsync();
                if (text.Length > 0 && text[0] == '<')
                {
                    Console.WriteLine("[Inv] Non-JSON inventory payload (HTML) from " + url);
                    return null;
                }

                return JsonConvert.DeserializeObject<InvResponse>(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Inv] Error " + ex.Message + " at " + url);
                return null;
            }
        }


        private async Task<PureSnapshot> GetPureSnapshotAsync()
        {
            // serve cached within 60s
            if (_pureCache != null && (DateTime.UtcNow - _pureCacheUtc) < TimeSpan.FromSeconds(60))
                return _pureCache;

            PureSnapshot snap = new PureSnapshot();

            InvResponse? inv = await FetchCommunityInventoryAsync();
            if (inv == null)
            {
                Console.WriteLine("[Inv] inventory unavailable; assuming zero pure.");
                _pureCache = snap;
                _pureCacheUtc = DateTime.UtcNow;
                return snap;
            }

            var dx = new Dictionary<string, InvDesc>(StringComparer.Ordinal);
            foreach (var d in inv.descriptions)
                dx[d.classid + "_" + d.instanceid] = d;

            foreach (var a in inv.assets)
            {
                if (!dx.TryGetValue(a.classid + "_" + a.instanceid, out var d)) continue;
                string n = !string.IsNullOrEmpty(d.market_hash_name) ? d.market_hash_name : d.name;

                if (NameCmp.Equals(n, "Mann Co. Supply Crate Key")) snap.Keys++;
                else if (NameCmp.Equals(n, "Refined Metal")) snap.Refined++;
                else if (NameCmp.Equals(n, "Reclaimed Metal")) snap.Reclaimed++;
                else if (NameCmp.Equals(n, "Scrap Metal")) snap.Scrap++;
            }

            _pureCache = snap;
            _pureCacheUtc = DateTime.UtcNow;
            Console.WriteLine($"[Inv] Keys={snap.Keys} Ref={snap.Refined} Rec={snap.Reclaimed} Scr={snap.Scrap}");
            return snap;
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


        // ---- community inventory (pure snapshot) ----

        private static readonly HttpStatusCode[] TransientHttp =
{
    HttpStatusCode.BadRequest,     // Steam CF sometimes 400s this endpoint
    (HttpStatusCode)429,           // rate limited
    HttpStatusCode.ServiceUnavailable,
    HttpStatusCode.GatewayTimeout
};

        private sealed class EconItemsResponse
        {
            public EconResult? result { get; set; }
        }
        private sealed class EconResult
        {
            public int status { get; set; }
            public List<EconItem>? items { get; set; }
        }
        private sealed class EconItem
        {
            public string id { get; set; } = "";       // ← TF2 assetid
            public int defindex { get; set; }          // 5021=key, 5002=ref, 5001=rec, 5000=scrap
            public int quantity { get; set; } = 1;
        }

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
        private sealed class PureSnapshot
        {
            public int Keys { get; set; }
            public int Refined { get; set; }
            public int Reclaimed { get; set; }
            public int Scrap { get; set; }
        }

        // ---- Stop/Dispose ----

        public void Stop()
        {
            System.Threading.CancellationTokenSource? cts = System.Threading.Interlocked.Exchange(ref _cts, null);
            if (cts == null) return;

            try { cts.Cancel(); } catch { }

            try
            {
                if (_timer != null)
                {
                    _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
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
    }
}
