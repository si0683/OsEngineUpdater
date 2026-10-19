using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Настройки одной копии OsEngine. Формат совместим со старым UpdateOsEngine.config.json.
    /// </summary>
    public sealed class UpdaterConfig
    {
        public const string ProfilePrebuilt = "Prebuilt";
        public const string ProfileFromSource = "FromSource";
        public const string ModeMirror = "Mirror";
        public const string ModeDelta = "Delta";
        public const string DefaultBuildCommand = "dotnet build \"project\\OsEngine\\OsEngine.csproj\" -c Debug";

        public string Owner { get; set; } = "AlexWan";
        public string Repo { get; set; } = "OsEngine";
        public string Branch { get; set; } = "master";
        /// <summary>Токен GitHub, зашифрованный для текущего пользователя Windows (DPAPI), base64.</summary>
        public string GitHubTokenProtected { get; set; } = "";

        /// <summary>Открытый токен из старых конфигов: при загрузке шифруется и больше не записывается.</summary>
        [JsonPropertyName("GitHubToken")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LegacyGitHubToken { get; set; }

        [JsonIgnore]
        public string GitHubToken
        {
            get => Secret.Unprotect(GitHubTokenProtected);
            set
            {
                GitHubTokenProtected = Secret.Protect(value);
                LegacyGitHubToken = null;
            }
        }

        /// <summary>Prebuilt — bin обновляется; FromSource — bin защищён, после обновления сборка.</summary>
        public string Profile { get; set; } = ProfilePrebuilt;

        /// <summary>Режим автообновления: Mirror (полная сверка) или Delta (только изменения).</summary>
        public string AutoMode { get; set; } = ModeMirror;

        public int AutoIntervalMinutes { get; set; } = 1440;

        public List<string> UserProtectedPaths { get; set; } = new List<string>();

        /// <summary>null — по профилю.</summary>
        public bool? BuildAfterUpdate { get; set; }

        /// <summary>Пусто — команда по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string BuildCommand { get; set; }

        /// <summary>Устаревшее поле старых конфигов, читается как дополнительные исключения.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> ProtectedPaths { get; set; }

        public const string DefaultRobotsDir = @"project\OsEngine\Robots";
        public const string DefaultIndicatorsDir = @"project\OsEngine\Indicators";

        /// <summary>Куда ставить роботов из своих репозиториев (относительно корня копии).</summary>
        public string RobotsDir { get; set; } = DefaultRobotsDir;

        /// <summary>Куда ставить индикаторы из своих репозиториев (относительно корня копии).</summary>
        public string IndicatorsDir { get; set; } = DefaultIndicatorsDir;

        /// <summary>Куда по умолчанию ставить «свои файлы» (относительно корня копии), структура репозитория сохраняется.</summary>
        public string FilesDir { get; set; } = DefaultFilesDir;

        public const string DefaultFilesDir = @"project\OsEngine";

        [JsonIgnore]
        public string EffectiveFilesDir => string.IsNullOrWhiteSpace(FilesDir) ? DefaultFilesDir : FilesDir.Trim();

        [JsonIgnore]
        public string EffectiveRobotsDir => string.IsNullOrWhiteSpace(RobotsDir) ? DefaultRobotsDir : RobotsDir.Trim();

        [JsonIgnore]
        public string EffectiveIndicatorsDir => string.IsNullOrWhiteSpace(IndicatorsDir) ? DefaultIndicatorsDir : IndicatorsDir.Trim();

        [JsonIgnore]
        public bool BinProtected => string.Equals(Profile, ProfileFromSource, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool ShouldBuild => BuildAfterUpdate ?? BinProtected;

        [JsonIgnore]
        public int IntervalMinutes => AutoIntervalMinutes < 1 ? 1440 : AutoIntervalMinutes;

        [JsonIgnore]
        public bool IsDeltaMode => string.Equals(AutoMode, ModeDelta, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public string EffectiveBuildCommand => string.IsNullOrWhiteSpace(BuildCommand) ? DefaultBuildCommand : BuildCommand;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static UpdaterConfig Load(CopyInfo copy)
        {
            string path = copy.ConfigPath;

            if (!File.Exists(path) && copy.LegacyConfigPath != null)
            {
                // перенос старого конфига из папки утилиты
                path = copy.LegacyConfigPath;
            }

            UpdaterConfig cfg = null;

            if (File.Exists(path))
            {
                cfg = JsonSerializer.Deserialize<UpdaterConfig>(File.ReadAllText(path), JsonOptions);
            }

            cfg ??= new UpdaterConfig();
            cfg.UserProtectedPaths ??= new List<string>();
            cfg.Owner = string.IsNullOrWhiteSpace(cfg.Owner) ? "AlexWan" : cfg.Owner.Trim();
            cfg.Repo = string.IsNullOrWhiteSpace(cfg.Repo) ? "OsEngine" : cfg.Repo.Trim();
            cfg.Branch = string.IsNullOrWhiteSpace(cfg.Branch) ? "master" : cfg.Branch.Trim();
            cfg.GitHubTokenProtected ??= "";

            // старый конфиг хранил токен открытым текстом — шифруем и перезаписываем файл без него
            bool migrateToken = cfg.LegacyGitHubToken != null;

            if (migrateToken)
            {
                string plain = cfg.LegacyGitHubToken;
                cfg.LegacyGitHubToken = null;

                if (!string.IsNullOrWhiteSpace(plain) && string.IsNullOrEmpty(cfg.GitHubTokenProtected))
                {
                    cfg.GitHubToken = plain;
                }
            }

            if (migrateToken || !File.Exists(copy.ConfigPath))
            {
                try
                {
                    cfg.Save(copy);
                }
                catch
                {
                    // не удалось перезаписать (нет прав) — работаем с тем, что прочитали
                }
            }

            return cfg;
        }

        public void Save(CopyInfo copy)
        {
            Directory.CreateDirectory(copy.DataDir);
            File.WriteAllText(copy.ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }

        public UpdaterConfig Clone()
        {
            return JsonSerializer.Deserialize<UpdaterConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions);
        }
    }
}
