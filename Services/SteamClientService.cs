using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using ArkRoxBot.Interfaces;

namespace ArkRoxBot.Services
{
    public sealed class SteamClientService : ISteamClientService
    {
        // SteamKit handlers
        private readonly SteamClient _client;
        private readonly CallbackManager _callbackManager;
        private readonly SteamUser _user;
        private readonly SteamFriends _friends;

        // For Dispose of the app
        private CancellationTokenSource? _pumpCts;
        private Task? _pumpTask;
        private readonly System.Threading.ManualResetEventSlim _loggedOffSignal = new System.Threading.ManualResetEventSlim(false);
        private readonly System.Threading.ManualResetEventSlim _disconnectedSignal = new System.Threading.ManualResetEventSlim(false);

        // Presence keepalive
        private System.Threading.Timer? _presenceTimer;
        private readonly TimeSpan _presencePeriod = TimeSpan.FromMinutes(1);

        private readonly TimeSpan _presenceBootstrapPeriod = TimeSpan.FromSeconds(45);
        private const int PresenceBootstrapMaxTicks = 8; // ~6 minutes of nudging
        private int _presenceBootstrapTicks = 0;



        // Bubble friend messages to bot logic
        public event Action<string, string>? OnFriendMessage;

        // Credentials provided at connect time
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string? _twoFactorCode = null;

        // Friend management
        private readonly Dictionary<string, DateTime> _lastMessageUtcBySteamId = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _whitelistSteamIds = new HashSet<string> { "7656119XXXXXXXXXX" };
        private readonly Random _random = new Random();
        private int _maxFriends = 200;

        public SteamClientService()
        {
            _client = new SteamClient();
            _callbackManager = new CallbackManager(_client);
            _user = _client.GetHandler<SteamUser>();
            _friends = _client.GetHandler<SteamFriends>();

            // Core subscriptions (no sentry / no login-key handlers)
            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            _callbackManager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMessageReceived);
            _callbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsListUpdated);
            _callbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaStateChanged);
            _callbackManager.Subscribe<SteamFriends.FriendAddedCallback>(OnFriendAdded);
            _callbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);

        }

        public Task ConnectAndLoginAsync(string username, string password, string? twoFactor = null)
        {
            _username = username ?? string.Empty;
            _password = password ?? string.Empty;
            _twoFactorCode = string.IsNullOrWhiteSpace(twoFactor) ? null : twoFactor.Trim();

            // If already pumping, do nothing.
            if (_pumpTask != null && !_pumpTask.IsCompleted)
            {
                return Task.CompletedTask;
            }

            // Reset shutdown signals for this session
            _loggedOffSignal.Reset();
            _disconnectedSignal.Reset();

            Console.WriteLine("Steam: Connecting…");
            _client.Connect();

            // Start token-based pump
            _pumpCts?.Dispose();
            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));

            return Task.CompletedTask;
        }

        private async Task PumpAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                await Task.Yield();
            }
        }

        public void Disconnect()
        {
            // Prefer StopAsync from callers; keep this as a hard fallback.
            try
            {
                StopPresenceKeepalive();
                try { _friends.SetPersonaState(EPersonaState.Offline); } catch { }
                try { System.Threading.Thread.Sleep(500); } catch { }
                try { _user.LogOff(); } catch { }
                _client.Disconnect();
            }
            catch { }
        }

        private void NudgePersona()
        {
            try
            {
                // A small “toggle” often refreshes the community profile faster.
                _friends.SetPersonaState(EPersonaState.Online);
                try { Thread.Sleep(150); } catch { }
                _friends.SetPersonaState(EPersonaState.LookingToTrade);
            }
            catch { }
        }
        private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            // Called when our own persona/account info is ready; reassert state here.
            Console.WriteLine("Steam: AccountInfo received → nudging persona.");
            NudgePersona();
        }


        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Steam: Logged off → " + callback.Result);
            StopPresenceKeepalive();
            _loggedOffSignal.Set();
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Steam: Disconnected.");
            StopPresenceKeepalive();
            _disconnectedSignal.Set();
        }

        private async Task ForceOfflineFlushAsync(TimeSpan duration)
        {
            DateTime until = DateTime.UtcNow.Add(duration);

            while (DateTime.UtcNow < until)
            {
                try { _friends.SetPersonaState(EPersonaState.Invisible); } catch { }
                try { await Task.Delay(400); } catch { }

                try { _friends.SetPersonaState(EPersonaState.Offline); } catch { }
                try { await Task.Delay(600); } catch { }
            }
        }




        public async Task StopAsync(TimeSpan? timeout = null)
        {
            TimeSpan wait = timeout ?? TimeSpan.FromSeconds(20);
            Console.WriteLine("Steam: Shutting down…");

            // Stop keepalive first so nothing flips us back online
            StopPresenceKeepalive();

            // 1) Repeatedly assert "Offline" while the callback pump is still running
            try { await ForceOfflineFlushAsync(TimeSpan.FromSeconds(12)); } catch { }

            // 2) Log off and wait for LoggedOff
            try
            {
                try { _user.LogOff(); } catch { }
                bool gotLoggedOff = _loggedOffSignal.Wait(wait);
                Console.WriteLine("Steam: LoggedOff wait → " + (gotLoggedOff ? "OK" : "timeout"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SteamClientService] Stop: logoff wait error: " + ex.Message);
            }

            // 3) Disconnect and wait
            try
            {
                _client.Disconnect();
                bool gotDisconnected = _disconnectedSignal.Wait(wait);
                Console.WriteLine("Steam: Disconnect wait → " + (gotDisconnected ? "OK" : "timeout"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SteamClientService] Stop: disconnect error: " + ex.Message);
            }

            // 4) Cancel the callback pump last
            try
            {
                _pumpCts?.Cancel();
                if (_pumpTask != null) { await Task.WhenAny(_pumpTask, Task.Delay(wait)); }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SteamClientService] Stop: pump cancel/wait error: " + ex.Message);
            }
            finally
            {
                _pumpCts?.Dispose(); _pumpCts = null;
                _pumpTask = null;
            }

            Console.WriteLine("[SteamClientService] Stopped gracefully.");
        }




        // -------------------------
        // Presence keepalive
        // -------------------------
        private void StartPresenceKeepalive()
        {
            StopPresenceKeepalive();

            _presenceBootstrapTicks = 0;

            // Bootstrap phase: hit every 45s to push community profile to Online quickly.
            _presenceTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    NudgePersona();

                    int t = System.Threading.Interlocked.Increment(ref _presenceBootstrapTicks);
                    if (t >= PresenceBootstrapMaxTicks && _presenceTimer != null)
                    {
                        // Switch to steady state (every 4 minutes)
                        _presenceTimer.Change(_presencePeriod, _presencePeriod);
                        Console.WriteLine("[Steam] Presence keepalive → steady (" + _presencePeriod.TotalMinutes.ToString() + "m).");
                    }
                }
                catch { }
            }, null, TimeSpan.Zero, _presenceBootstrapPeriod);

            Console.WriteLine("[Steam] Presence keepalive started (bootstrap " +
                              _presenceBootstrapPeriod.TotalSeconds.ToString() + "s × " +
                              PresenceBootstrapMaxTicks.ToString() + " → steady " +
                              _presencePeriod.TotalMinutes.ToString() + "m).");
        }

        private void StopPresenceKeepalive()
        {
            try
            {
                if (_presenceTimer != null)
                {
                    _presenceTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    _presenceTimer.Dispose();
                    _presenceTimer = null;
                }
                Console.WriteLine("[Steam] Presence keepalive stopped.");
            }
            catch { }
        }

        // -------------------------
        // Login (password + 2FA only)
        // -------------------------
        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Console.WriteLine("Steam: Connected. Logging in with password + 2FA…");

            SteamUser.LogOnDetails details = new SteamUser.LogOnDetails
            {
                Username = _username,
                Password = _password,
                TwoFactorCode = _twoFactorCode
            };

            _user.LogOn(details);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                Console.WriteLine("Steam: Logged on successfully.");
                try
                {
                    _friends.SetPersonaState(EPersonaState.Online);
                    _friends.SetPersonaState(EPersonaState.LookingToTrade); // nudge community profile
                    StartPresenceKeepalive();
                }
                catch { }

                StartPresenceKeepalive();
                AcceptAnyPendingInvites();
                return;
            }

            if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
                Console.WriteLine("Steam: 2FA required (AccountLoginDeniedNeedTwoFactor). Provide a fresh code and reconnect.");
            else if (callback.Result == EResult.AccountLogonDenied)
                Console.WriteLine("Steam: Email code required (AccountLogonDenied). Provide the code and reconnect.");
            else
                Console.WriteLine("Steam: Login failed → " + callback.Result + " (extended: " + callback.ExtendedResult + ")");
        }

        // -----------------
        // Friends & chatting
        // -----------------
        public void SendMessage(string steamId64, string text)
        {
            SteamID steamId = new SteamID(Convert.ToUInt64(steamId64));
            string safeText = text ?? string.Empty;
            _friends.SendChatMessage(steamId, EChatEntryType.ChatMsg, safeText);
        }

        private void OnPersonaStateChanged(SteamFriends.PersonaStateCallback callback)
        {
            SteamID who = callback.FriendID;
            EFriendRelationship rel = _friends.GetFriendRelationship(who);
            Console.WriteLine("PersonaState: " + who.ConvertToUInt64() + " rel=" + rel);
        }

        private void OnFriendAdded(SteamFriends.FriendAddedCallback callback)
        {
            string sid = callback.SteamID.ConvertToUInt64().ToString();

            if (callback.Result == EResult.OK)
            {
                Console.WriteLine("Friend added: " + sid + " → sending welcome.");
                SendMessage(sid, "Hey! I’m a trading bot. Type !help for commands.");
                _lastMessageUtcBySteamId[sid] = DateTime.UtcNow;
            }
            else
            {
                Console.WriteLine("AddFriend failed for " + sid + " → " + callback.Result);
            }
        }

        private void AcceptAnyPendingInvites()
        {
            int total = _friends.GetFriendCount();
            for (int i = 0; i < total; i++)
            {
                SteamID id = _friends.GetFriendByIndex(i);
                if (_friends.GetFriendRelationship(id) == EFriendRelationship.RequestRecipient)
                {
                    Console.WriteLine("Accepting pending invite (startup) from " + id.ConvertToUInt64());
                    _friends.AddFriend(id);
                    try { Thread.Sleep(500); } catch { }
                    SendMessage(id.ConvertToUInt64().ToString(), "Hey! I’m a trading bot. Type !help for commands.");
                }
            }

            PruneFriendsIfNeeded();
        }

        private void OnFriendMessageReceived(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType != EChatEntryType.ChatMsg)
            {
                return;
            }

            string steamId64 = callback.Sender.ConvertToUInt64().ToString();
            string message = callback.Message;

            _lastMessageUtcBySteamId[steamId64] = DateTime.UtcNow;

            Console.WriteLine("MSG <" + steamId64 + ">: " + message);
            Action<string, string>? handler = OnFriendMessage;
            if (handler != null)
            {
                handler.Invoke(steamId64, message);
            }
        }

        private void OnFriendsListUpdated(SteamFriends.FriendsListCallback callback)
        {
            foreach (SteamFriends.FriendsListCallback.Friend friend in callback.FriendList)
            {
                if (friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    SteamID id = friend.SteamID;
                    Console.WriteLine("Friend request from " + id.ConvertToUInt64() + " → accepting.");
                    _friends.AddFriend(id);
                    try { Thread.Sleep(500); } catch { }
                    SendMessage(id.ConvertToUInt64().ToString(), "Hey! I’m a trading bot. Type !help for commands.");
                }
            }

            PruneFriendsIfNeeded();
        }

        private void PruneFriendsIfNeeded()
        {
            int totalKnown = _friends.GetFriendCount();
            List<SteamID> allFriends = new List<SteamID>(totalKnown);

            for (int i = 0; i < totalKnown; i++)
            {
                SteamID friendId = _friends.GetFriendByIndex(i);
                if (_friends.GetFriendRelationship(friendId) == EFriendRelationship.Friend)
                {
                    allFriends.Add(friendId);
                }
            }

            int friendCount = allFriends.Count;
            if (friendCount <= _maxFriends)
            {
                return;
            }

            DateTime activeCutoffUtc = DateTime.UtcNow.AddDays(-3);
            List<SteamID> removableCandidates = new List<SteamID>();

            foreach (SteamID id in allFriends)
            {
                string steamId64 = id.ConvertToUInt64().ToString();
                if (_whitelistSteamIds.Contains(steamId64))
                {
                    continue;
                }

                DateTime lastMessageUtc;
                bool hasMessage = _lastMessageUtcBySteamId.TryGetValue(steamId64, out lastMessageUtc);
                if (hasMessage && lastMessageUtc >= activeCutoffUtc)
                {
                    continue; // recently active → keep
                }

                removableCandidates.Add(id);
            }

            if (removableCandidates.Count == 0)
            {
                return;
            }

            while (friendCount > _maxFriends && removableCandidates.Count > 0)
            {
                int pickIndex = _random.Next(removableCandidates.Count);
                SteamID removeId = removableCandidates[pickIndex];
                removableCandidates.RemoveAt(pickIndex);

                string steamId64 = removeId.ConvertToUInt64().ToString();
                Console.WriteLine("Pruning friend " + steamId64 + " to stay under " + _maxFriends + ".");

                SendMessage(
                    steamId64,
                    "Hi! I'm cleaning up my friends list. If you want to trade again, feel free to re-add me anytime."
                );

                _friends.RemoveFriend(removeId);
                friendCount--;
            }
        }
    }
}
