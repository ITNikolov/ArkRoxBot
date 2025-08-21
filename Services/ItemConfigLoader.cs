using System.Text.Json;
using ArkRoxBot.Models.Config;

namespace ArkRoxBot.Services
{
    public class ItemConfigLoader
    {
        private readonly string _filePath;

        public ItemConfigLoader(string filePath)
        {
            _filePath = filePath;
        }

        public ConfigRoot LoadItems()
        {
            if (!File.Exists(_filePath))
            {
                Console.WriteLine($"Error: Could not find config file at {_filePath}");
                return new ConfigRoot();
            }

            string json = File.ReadAllText(_filePath);

            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            ConfigRoot? config = JsonSerializer.Deserialize<ConfigRoot>(json, options);
            return config ?? new ConfigRoot();
        }
    }
}
