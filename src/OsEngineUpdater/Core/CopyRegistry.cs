using System.Text.Json;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Список копий OsEngine, которыми управляет утилита (%LOCALAPPDATA%\OsEngineUpdater\copies.json).
    /// </summary>
    public static class CopyRegistry
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsEngineUpdater", "copies.json");

        public static List<string> Load()
        {
            List<string> roots = new List<string>();

            try
            {
                if (File.Exists(FilePath))
                {
                    roots = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new List<string>();
                }
            }
            catch
            {
                // испорченный список — начнём заново
            }

            return roots;
        }

        public static void Save(IEnumerable<string> roots)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(roots.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Сохранённый список + копия, в которой лежит сам exe, + копии из заданий Планировщика.
        /// Несуществующие копии отбрасываются.
        /// </summary>
        public static List<string> LoadWithDiscovery()
        {
            List<string> result = new List<string>();

            void Add(string root)
            {
                if (root == null || !CopyInfo.IsCopyRoot(root))
                {
                    return;
                }

                string norm = CopyInfo.NormalizeRoot(root);

                if (!result.Any(r => string.Equals(r, norm, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(norm);
                }
            }

            foreach (string r in Load())
            {
                Add(r);
            }

            Add(CopyInfo.FindRoot(AppContext.BaseDirectory));

            foreach (string r in AutoTaskService.DiscoverRoots())
            {
                Add(r);
            }

            Save(result);
            return result;
        }
    }
}
