using System;
using System.Threading.Tasks;
using SteamKit2;
using ArkRoxBot.Interfaces;
using SteamKit2.WebUI.Internal;

namespace ArkRoxBot.Services
{
    public class SteamClientService : ISteamClientService
    {
        private readonly SteamClient _client;
        private readonly CallbackManager _callbacks;
        private readonly SteamUser _user;
        private readonly SteamFriends _friends;

        public event Action<string, string>? OnFriendMessage;

        private string _username = "";
        private string _password = "";
        private string? _twoFactor = null;

        private bool _keepPumping = false;

        private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastMessageUtc
    = new System.Collections.Generic.Dictionary<string, DateTime>();

        // Protect these accounts from being removed
        private readonly System.Collections.Generic.HashSet<string> _whitelist
            = new System.Collections.Generic.HashSet<string>
            {
                "7656119XXXXXXXXXX"
            };

        private int _maxFriends = 250;

        // RNG for random pruning
        private readonly System.Random _rng = new System.Random();



        public SteamClientService()
        {
            _client = new SteamClient();
            _callbacks = new CallbackManager(_client);
            _user = _client.GetHandler<SteamUser>();
            _friends = _client.GetHandler<SteamFriends>();

            // SteamKit2 v3 pattern:
            _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            _callbacks.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);
            _callbacks.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);

        }

        public async Task ConnectAndLoginAsync(string username, string password, string? twoFactor = null)
        {
            _username = username;
            _password = password;
            _twoFactor = twoFactor;

            Console.WriteLine("Steam: Connecting…");
            _keepPumping = true;
            _client.Connect();

            // Keep pumping until explicitly stopped (so we can receive messages)
            await Task.Run(() =>
            {
                while (_keepPumping)
                {
                    _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                }
            });
        }

        // NEW: send a chat message back
        public void SendMessage(string steamId64, string text)
        {
            SteamID id = new SteamID(Convert.ToUInt64(steamId64));
            _friends.SendChatMessage(id, EChatEntryType.ChatMsg, text ?? string.Empty);
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Console.WriteLine("Steam: Connected. Logging in…");

            SteamUser.LogOnDetails details = new SteamUser.LogOnDetails
            {
                Username = _username,
                Password = _password,
                TwoFactorCode = _twoFactor
            };

            _user.LogOn(details);
        }

        private void OnFriendsList(SteamFriends.FriendsListCallback callback)
        {
            // Accept any inbound friend requests
            foreach (SteamFriends.FriendsListCallback.Friend f in callback.FriendList)
            {
                if (f.Relationship == EFriendRelationship.RequestRecipient)
                {
                    SteamID id = f.SteamID;
                    Console.WriteLine("Friend request from " + id.ConvertToUInt64() + " → accepting.");
                    _friends.AddFriend(id);

                    // tiny pause so Steam applies the relationship
                    System.Threading.Thread.Sleep(500);

                    // Send a short welcome
                    SendMessage(id.ConvertToUInt64().ToString(),
                        "Hey! I’m a trading bot. Type !help for commands.");
                }

            }

            // After accepting requests, keep list under the cap
            PruneFriendsIfNeeded();

        }


        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Steam: Disconnected.");
            _keepPumping = false;
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                Console.WriteLine("Steam: Logged on successfully.");
                _friends.SetPersonaState(EPersonaState.Online);
                // NOTE: Do NOT set _keepPumping = false; we want to keep receiving messages.
            }
            else
            {
                Console.WriteLine("Steam: Login failed → " + callback.Result + " (extended: " + callback.ExtendedResult + ")");
                _keepPumping = false;
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Steam: Logged off → " + callback.Result);
            _keepPumping = false;
        }
        private void PruneFriendsIfNeeded()
        {
            int total = _friends.GetFriendCount();
            System.Collections.Generic.List<SteamID> allFriends =
                new System.Collections.Generic.List<SteamID>(total);

            for (int i = 0; i < total; i++)
            {
                SteamID id = _friends.GetFriendByIndex(i);
                if (_friends.GetFriendRelationship(id) == EFriendRelationship.Friend)
                {
                    allFriends.Add(id);
                }
            }

            int friendTotal = allFriends.Count;
            if (friendTotal <= _maxFriends)
                return;

            DateTime cutoff = DateTime.UtcNow.AddDays(-3);
            System.Collections.Generic.List<SteamID> candidates =
                new System.Collections.Generic.List<SteamID>();

            foreach (SteamID id in allFriends)
            {
                string sid = id.ConvertToUInt64().ToString();
                if (_whitelist.Contains(sid))
                    continue;

                DateTime last;
                bool hasActivity = _lastMessageUtc.TryGetValue(sid, out last);
                if (hasActivity && last >= cutoff)
                    continue;

                candidates.Add(id);
            }

            if (candidates.Count == 0)
                return;

            while (friendTotal > _maxFriends && candidates.Count > 0)
            {
                int index = _rng.Next(candidates.Count);
                SteamID removeId = candidates[index];
                candidates.RemoveAt(index);

                string sid = removeId.ConvertToUInt64().ToString();
                Console.WriteLine("Pruning friend " + sid + " to keep list under " + _maxFriends + ".");

                // NEW: notify them before removal
                SendMessage(sid,
                    "Hi! I'm cleaning up my friends list to stay under the Steam limit. " +
                    "If you want to trade again, feel free to re-add me anytime.");

                _friends.RemoveFriend(removeId);

                friendTotal--;
            }

        }


        private void OnFriendMsg(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType != EChatEntryType.ChatMsg)
                return;

            string steamId64 = callback.Sender.ConvertToUInt64().ToString();
            string text = callback.Message;

            _lastMessageUtc[steamId64] = DateTime.UtcNow;

            Console.WriteLine("MSG <" + steamId64 + ">: " + text);

            if (OnFriendMessage != null)
            {
                OnFriendMessage.Invoke(steamId64, text);
            }
        }


    }
}
