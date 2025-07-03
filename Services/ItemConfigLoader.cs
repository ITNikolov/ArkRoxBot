using ArkRoxBot.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Services
{
    internal class ItemConfigLoader
    {
        private readonly string _filePath;

        public ItemConfigLoader(string filePath)
        {
            _filePath = filePath;
        }


        public List<ItemConfig> LoadItems()
        {
            if (!File.Exists(_filePath))
            {
                Console.WriteLine($"Error: Could not find the items file at {_filePath}");
                return new List<ItemConfig>();
            }

            string json = File.ReadAllText(_filePath);

            List<ItemConfig>? items = JsonConvert.DeserializeObject<List<ItemConfig>>(json);

            return items ?? new List<ItemConfig>();
        }
    }
}
