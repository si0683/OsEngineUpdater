using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsEngineUpdater.Core
{
    /// <summary>Что лежит в репозитории и куда его класть в копии.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PackageKind
    {
        /// <summary>Робот: в &lt;папка роботов копии&gt;\&lt;имя&gt;.</summary>
        Robot,
        /// <summary>Индикатор: в &lt;папка индикаторов копии&gt;\&lt;имя&gt;.</summary>
        Indicator,
        /// <summary>Свои файлы: раскладываются в указанную папку копии (можно поверх файлов OsEngine).</summary>
        Files
    }

    /// <summary>Ваш репозиторий (GitHub, GitFlic или любой git) с роботом, индикатором или своими файлами.</summary>
    public sealed class PackageSource
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Имя в списке и имя папки для робота/индикатора. Пусто — имя репозитория.</summary>
        public string Name { get; set; } = "";

        /// <summary>Адрес репозитория, например https://github.com/user/MyRobot или https://gitflic.ru/project/user/myrobot.</summary>
        public string Url { get; set; } = "";

        /// <summary>Ветка. Пусто — ветка по умолчанию.</summary>
        public string Branch { get; set; } = "";

        /// <summary>Папка внутри репозитория, которую брать. Пусто — весь репозиторий.</summary>
        public string SubPath { get; set; } = "";

        public PackageKind Kind { get; set; } = PackageKind.Robot;

        /// <summary>Для «Свои файлы»: папка копии (относительно корня), куда раскладывать.</summary>
        public string TargetPath { get; set; } = @"project\OsEngine";

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? NameFromUrl(Url) : Name.Trim();

        [JsonIgnore]
        public string KindText => KindToText(Kind);

        public static string KindToText(PackageKind kind) => kind switch
        {
            PackageKind.Robot => "Робот",
            PackageKind.Indicator => "Индикатор",
            _ => "Свои файлы"
        };

        public static string NameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "без имени";
            }

            string s = url.Trim().TrimEnd('/');

            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - 4);
            }

            int i = s.LastIndexOfAny(new[] { '/', ':' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        /// <summary>Адрес для git clone. Для GitFlic ссылка на страницу проекта тоже подходит, дописываем .git.</summary>
        [JsonIgnore]
        public string CloneUrl
        {
            get
            {
                string s = Url.Trim().TrimEnd('/');

                if (s.StartsWith("https://gitflic.ru/project/", StringComparison.OrdinalIgnoreCase)
                    && !s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    s += ".git";
                }

                return s;
            }
        }
    }

    /// <summary>
    /// Общие настройки программы (для всех копий): %LOCALAPPDATA%\OsEngineUpdater\settings.json.
    /// </summary>
    public sealed class UserSettings
    {
        public List<PackageSource> Sources { get; set; } = new List<PackageSource>();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsEngineUpdater", "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    UserSettings s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), JsonOptions);

                    if (s != null)
                    {
                        s.Sources ??= new List<PackageSource>();
                        return s;
                    }
                }
            }
            catch
            {
                // испорченный файл откладываем, чтобы не затереть его при следующем сохранении
                try
                {
                    File.Copy(FilePath, FilePath + ".bad", true);
                }
                catch
                {
                    // не удалось — начнём с пустых настроек
                }
            }

            return new UserSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }

        public UserSettings Clone()
        {
            return JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions);
        }
    }
}
