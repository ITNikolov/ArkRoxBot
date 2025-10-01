using System;
using System.Threading;
using System.Threading.Tasks;
using ArkRoxBot.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ArkRoxBot.Services
{
    public sealed class BotApp : IHostedService
    {
        private readonly ISteamClientService _steam;
        private readonly BotService _bot;
        private readonly ITradeService _trades;
        private readonly CommandService _commands;
        private readonly IConfiguration _cfg;

        private Task? _botRunTask;

        public BotApp(ISteamClientService steam, BotService bot, ITradeService trades, CommandService commands, IConfiguration cfg)
        {
            _steam = steam;
            _bot = bot;
            _trades = trades;
            _commands = commands;
            _cfg = cfg;
        }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            string? username = _cfg["Steam:Username"] ?? Environment.GetEnvironmentVariable("STEAM_USERNAME");
            string? password = _cfg["Steam:Password"] ?? Environment.GetEnvironmentVariable("STEAM_PASSWORD");
            string? twoFactor = _cfg["Steam:TwoFactor"]; 

            bool interactive = !Console.IsInputRedirected;

            if (string.IsNullOrWhiteSpace(username) && interactive)
            {
                Console.Write("Steam username: ");
                username = Console.ReadLine() ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(password) && interactive)
            {
                password = ReadMasked("Steam password: ");
            }
            if (string.IsNullOrWhiteSpace(twoFactor) && interactive)
            {
                twoFactor = ReadMasked("Steam 2FA (if any): ");
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException(
                    "Steam credentials missing. Provide Steam:Username and Steam:Password via appsettings or environment.");

            _steam.OnFriendMessage += (steamId64, msg) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await _commands.HandleCommandAsync(msg, steamId64);
                        if (!string.IsNullOrWhiteSpace(reply))
                            _steam.SendMessage(steamId64, reply);
                    }
                    catch (Exception ex)
                    {
                        _steam.SendMessage(steamId64, "Error: " + ex.Message);
                    }
                }, cancellationToken);
            };

            var loginTask = _steam.ConnectAndLoginAsync(username!, password!, twoFactor);
            _ = loginTask.ContinueWith(t =>
            {
                Console.WriteLine("[Steam] Login task faulted: " + (t.Exception?.GetBaseException().Message ?? "unknown"));
            }, TaskContinuationOptions.OnlyOnFaulted);


            _trades.Start();
            _ = _trades.VerifyCommunityCookiesOnceAsync();
            _ = _trades.LogStockSnapshotOnceAsync();

            _botRunTask = Task.Run(() => _bot.RunAsync(), cancellationToken);

            Console.WriteLine("Bot running. Press Ctrl+C or close window to stop.");
            return Task.CompletedTask;
        }



        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try { _trades.Stop(); } catch { }
            try { await _steam.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (_botRunTask != null)
                try { await Task.WhenAny(_botRunTask, Task.Delay(2000, cancellationToken)); } catch { }
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
