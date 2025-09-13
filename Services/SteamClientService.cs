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

        // Bubble friend messages to bot logic
        public event Action<string, string>? OnFriendMessage;

        // Credentials provided at connect time
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string? _twoFactorCode = null;

        // Callback pump
        private bool _shouldPumpCallbacks = false;

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

        }

        public async Task ConnectAndLoginAsync(string username, string password, string? twoFactorCode = null)
        {
            _username = username;
            _password = password;
            _twoFactorCode = twoFactorCode;

            Console.WriteLine("Steam: Connecting…");
            _shouldPumpCallbacks = true;
            _client.Connect();

            await Task.Run(() =>
            {
                while (_shouldPumpCallbacks)
                {
                    _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                }
            });
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
                Password = _password,          // always send password
                TwoFactorCode = _twoFactorCode // provide Steam Guard code each run
                // No SentryFileHash
                // No LoginKey
            };

            _user.LogOn(details);
        }
        // Fires when Steam updates persona/relationship for a single user.
        // If someone sent us a friend request, their relationship becomes RequestRecipient.
        private void OnPersonaStateChanged(SteamFriends.PersonaStateCallback callback)
        {
            SteamID who = callback.FriendID;
            EFriendRelationship rel = _friends.GetFriendRelationship(who);

            Console.WriteLine("PersonaState: " + who.ConvertToUInt64() + " rel=" + rel);

            if (rel == EFriendRelationship.RequestRecipient)
            {
                Console.WriteLine("Accepting friend request from " + who.ConvertToUInt64());
                _friends.AddFriend(who);
                // don’t greet here yet; wait for FriendAddedCallback so we’re definitely friends
            }
        }

        // Fires after Steam accepts our AddFriend (or fails).
        private void OnFriendAdded(SteamFriends.FriendAddedCallback callback)
        {
            string sid = callback.SteamID.ConvertToUInt64().ToString();

            if (callback.Result == EResult.OK)
            {
                Console.WriteLine("Friend added: " + sid + " → sending welcome.");
                SendMessage(sid, "Hey! I’m a trading bot. Type !help for commands.");
                _lastMessageUtcBySteamId[sid] = DateTime.UtcNow; // optional: mark activity
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
                }
            }
        }


        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                Console.WriteLine("Steam: Logged on successfully.");
                _friends.SetPersonaState(EPersonaState.Online);
                AcceptAnyPendingInvites();   // ← add this
                return;
            }

            if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
                Console.WriteLine("Steam: 2FA required (AccountLoginDeniedNeedTwoFactor). Provide a fresh code and reconnect.");
            else if (callback.Result == EResult.AccountLogonDenied)
                Console.WriteLine("Steam: Email code required (AccountLogonDenied). Provide the code and reconnect.");
            else
                Console.WriteLine("Steam: Login failed → " + callback.Result + " (extended: " + callback.ExtendedResult + ")");

            _shouldPumpCallbacks = false;
        }


        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Steam: Logged off → " + callback.Result);
            _shouldPumpCallbacks = false;
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Steam: Disconnected.");
            _shouldPumpCallbacks = false;
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

        private void OnFriendsListUpdated(SteamFriends.FriendsListCallback callback)
        {
            foreach (SteamFriends.FriendsListCallback.Friend friend in callback.FriendList)
            {
                if (friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    SteamID id = friend.SteamID;
                    Console.WriteLine("Friend request from " + id.ConvertToUInt64() + " → accepting.");
                    _friends.AddFriend(id);

                    // let Steam apply the relationship
                    Thread.Sleep(500);

                    SendMessage(id.ConvertToUInt64().ToString(),
                        "Hey! I’m a trading bot. Type !help for commands.");
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
