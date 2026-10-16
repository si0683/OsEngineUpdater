using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Одна копия OsEngine (папка, в которой лежит project\OsEngine\OsEngine.csproj).
    /// Служебные файлы копии лежат в &lt;копия&gt;\.osengine-updater\.
    /// </summary>
    public sealed class CopyInfo
    {
        public const string MarkerRelativePath = @"project\OsEngine\OsEngine.csproj";

        public string Root { get; }
        public string DataDir => Path.Combine(Root, ".osengine-updater");
        public string ConfigPath => Path.Combine(DataDir, "config.json");
        public string StatePath => Path.Combine(DataDir, "state.json");
        public string ManifestPath => Path.Combine(DataDir, "official-manifest.txt");
        public string LogPath => Path.Combine(DataDir, "update.log");
        public string BuiltInUpdaterStatePath => Path.Combine(Root, @"project\OsEngine\bin\Debug\Engine\Updater\LastUpdatesInfo.txt");

        /// <summary>Старый конфиг из папки утилиты (UpdateOsEngine.config.json), если есть.</summary>
        public string LegacyConfigPath
        {
            get
            {
                string p = Path.Combine(Root, "OsEngineUpdater", "UpdateOsEngine.config.json");
                return File.Exists(p) ? p : null;
            }
        }

        /// <summary>Имя для списка: «родитель\папка», чтобы различать копии с одинаковым именем папки.</summary>
        public string Name
        {
            get
            {
                string name = Path.GetFileName(Root);
                string parent = Path.GetDirectoryName(Root);

                if (string.IsNullOrEmpty(name))
                {
                    return Root;
                }

                if (string.IsNullOrEmpty(parent) || Path.GetPathRoot(parent) == parent)
                {
                    return name;
                }

                return Path.GetFileName(parent) + "\\" + name;
            }
        }

        /// <summary>Имя задания в Планировщике — то же, что у старой версии (OsEngineUpdate_&lt;hash&gt;).</summary>
        public string TaskName
        {
            get
            {
                byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant()));
                return "OsEngineUpdate_" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            }
        }

        public CopyInfo(string root)
        {
            Root = NormalizeRoot(root);
        }

        public static string NormalizeRoot(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);

            if (full.Length > root.Length)
            {
                full = full.TrimEnd('\\', '/');
            }

            return full;
        }

        public static bool IsCopyRoot(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MarkerRelativePath));
        }

        /// <summary>Ищет корень копии вверх от указанной папки.</summary>
        public static string FindRoot(string startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir))
            {
                return null;
            }

            DirectoryInfo d = new DirectoryInfo(Path.GetFullPath(startDir));

            while (d != null)
            {
                if (IsCopyRoot(d.FullName))
                {
                    return NormalizeRoot(d.FullName);
                }

                d = d.Parent;
            }

            return null;
        }

        #region State and manifest

        public UpdaterState LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    return JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(StatePath));
                }
            }
            catch
            {
                // испорченный state считаем отсутствующим
            }

            return null;
        }

        public void SaveState(string sha)
        {
            Directory.CreateDirectory(DataDir);
            UpdaterState state = new UpdaterState { LastSha = sha, UpdatedUtc = DateTime.UtcNow.ToString("o") };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        public List<string> LoadManifest()
        {
            if (!File.Exists(ManifestPath))
            {
                return new List<string>();
            }

            return File.ReadAllLines(ManifestPath).Where(l => l.Length > 0).ToList();
        }

        public void SaveManifest(IEnumerable<string> paths)
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(ManifestPath, paths, new UTF8Encoding(true));
        }

        #endregion

        public override string ToString() => Root;
    }

    public sealed class UpdaterState
    {
        public string LastSha { get; set; }
        public string UpdatedUtc { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? UpdatedLocal
        {
            get
            {
                if (DateTime.TryParse(UpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt))
                {
                    return dt.ToLocalTime();
                }

                return null;
            }
        }
    }
}
