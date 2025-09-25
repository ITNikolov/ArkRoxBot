using System;
using System.Threading.Tasks;

namespace ArkRoxBot.Interfaces
{
    public interface ISteamClientService
    {
        Task ConnectAndLoginAsync(string username, string password, string? twoFactor = null);

        // Raised on any friend message: (steamId64, text)
        event Action<string, string>? OnFriendMessage;

        Task StopAsync(TimeSpan? timeout = null);

        // Allow other services to send messages back
        void SendMessage(string steamId64, string text);

        
    }
}
