using System;
using System.Threading;
using System.Threading.Tasks;
using ArkRoxBot.Interfaces;

namespace ArkRoxBot.Services
{
    public sealed class BotApp : Microsoft.Extensions.Hosting.IHostedService
    {
        private readonly ISteamClientService _steam;
        private readonly BotService _bot;
        private readonly TradeService _trades;

        private Task? _botRunTask;

        public BotApp(ISteamClientService steam, BotService bot, TradeService trades)
        {
            _steam = steam;
            _bot = bot;
            _trades = trades;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Prompt (kept here so we don’t store creds)
            Console.Write("Steam username: ");
            string username = Console.ReadLine() ?? string.Empty;
            string password = ReadMasked("Steam password: ");
            string twoFactor = ReadMasked("Steam 2FA (if any): ");

            // Start Steam callback pump (don’t await)
            _ = _steam.ConnectAndLoginAsync(username, password, twoFactor);

            // Start trade poller
            _trades.Start();

            // Kick the pricing pipeline once in the background
            _botRunTask = Task.Run(() => _bot.RunAsync(), cancellationToken);

            Console.WriteLine("Bot running. Press Ctrl+C or close window to stop.");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try { _trades.Stop(); } catch { }
            try { await _steam.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (_botRunTask != null)
            {
                try { await Task.WhenAny(_botRunTask, Task.Delay(2000, cancellationToken)); } catch { }
            }
        }

        private static string ReadMasked(string prompt)
        {
            Console.Write(prompt);
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            return sb.ToString();
        }
    }
}
