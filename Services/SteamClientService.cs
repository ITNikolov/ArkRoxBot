using System;
using System.Threading.Tasks;
using SteamKit2;
using ArkRoxBot.Interfaces;

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

        private void OnFriendMsg(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType != EChatEntryType.ChatMsg)
                return;

            string steamId64 = callback.Sender.ConvertToUInt64().ToString();
            string text = callback.Message;

            Console.WriteLine("MSG <" + steamId64 + ">: " + text);

            if (OnFriendMessage != null)
            {
                OnFriendMessage.Invoke(steamId64, text);
            }
        }
    }
}
