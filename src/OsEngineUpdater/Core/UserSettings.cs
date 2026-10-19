using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsEngineUpdater.Core
{
    /// <summary>Куда ставить выбранное из своих репозиториев. Выбирается при установке.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PackageKind
    {
        /// <summary>Робот: &lt;папка роботов копии&gt;\&lt;репозиторий&gt;\...</summary>
        Robot,
        /// <summary>Индикатор: &lt;папка индикаторов копии&gt;\&lt;репозиторий&gt;\...</summary>
        Indicator,
        /// <summary>Свои файлы: в выбранную папку копии со структурой репозитория (можно поверх файлов OsEngine).</summary>
        Files
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GitProvider
    {
        GitHub,
        GitFlic,
        Other
    }

    /// <summary>
    /// Строка «Мои аккаунты и репозитории»: аккаунт (все его репозитории) или один репозиторий.
    /// Тип определяется по адресу.
    /// </summary>
    public sealed class SourceEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>https://github.com/si0683, https://gitflic.ru/user/si0683 или адрес одного репозитория.</summary>
        public string Url { get; set; } = "";

        /// <summary>Только для одного репозитория. Пусто — ветка по умолчанию.</summary>
        public string Branch { get; set; } = "";

        /// <summary>Токен, зашифрованный для текущего пользователя Windows (DPAPI), base64.</summary>
        public string TokenProtected { get; set; } = "";

        [JsonIgnore]
        public string Token
        {
            get => Unprotect(TokenProtected);
            set => TokenProtected = Protect(value);
        }

        [JsonIgnore]
        public bool HasToken => !string.IsNullOrEmpty(TokenProtected);

        [JsonIgnore]
        public GitProvider Provider => Parse(Url).Provider;

        /// <summary>Логин / организация (для аккаунта) или владелец репозитория.</summary>
        [JsonIgnore]
        public string Owner => Parse(Url).Owner;

        /// <summary>Имя репозитория; null — это аккаунт.</summary>
        [JsonIgnore]
        public string RepoName => Parse(Url).Repo;

        [JsonIgnore]
        public bool IsAccount => Parse(Url).Owner != null && Parse(Url).Repo == null && Provider != GitProvider.Other;

        [JsonIgnore]
        public string Title
        {
            get
            {
                (GitProvider p, string owner, string repo) = Parse(Url);

                if (string.IsNullOrWhiteSpace(Url)) return "новая запись";
                if (repo == null && owner != null) return $"{owner} — все репозитории";
                if (repo != null) return repo;
                return Url;
            }
        }

        [JsonIgnore]
        public string Subtitle
        {
            get
            {
                string kind = IsAccount ? "аккаунт" : "репозиторий";
                string host = Provider switch { GitProvider.GitHub => "GitHub", GitProvider.GitFlic => "GitFlic", _ => "git" };
                return $"{host} · {kind}{(HasToken ? " · токен" : "")}";
            }
        }

        /// <summary>Разбор адреса: GitHub (github.com/owner[/repo]), GitFlic (gitflic.ru/user|team|company/owner или gitflic.ru/project/owner/repo).</summary>
        public static (GitProvider Provider, string Owner, string Repo) Parse(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri u))
            {
                return (GitProvider.Other, null, null);
            }

            string[] seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s)
                .ToArray();

            string host = u.Host.ToLowerInvariant();

            if (host == "github.com" || host == "www.github.com")
            {
                return seg.Length switch
                {
                    0 => (GitProvider.GitHub, null, null),
                    1 => (GitProvider.GitHub, seg[0], null),
                    _ => (GitProvider.GitHub, seg[0], seg[1])
                };
            }

            if (host == "gitflic.ru" || host == "www.gitflic.ru")
            {
                if (seg.Length >= 3 && seg[0].Equals("project", StringComparison.OrdinalIgnoreCase))
                {
                    return (GitProvider.GitFlic, seg[1], seg[2]);
                }

                if (seg.Length >= 2 && (seg[0] is "user" or "team" or "company"))
                {
                    return (GitProvider.GitFlic, seg[1], null);
                }

                return (GitProvider.GitFlic, null, null);
            }

            // любой другой git-сервер — только адрес одного репозитория
            return (GitProvider.Other, seg.Length >= 1 ? seg[0] : null, seg.Length >= 1 ? seg[^1] : u.Host);
        }

        private static string Protect(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return "";
            }

            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        private static string Unprotect(string protectedToken)
        {
            if (string.IsNullOrEmpty(protectedToken))
            {
                return "";
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                // зашифровано под другим пользователем / на другом компьютере
                return "";
            }
        }
    }

    /// <summary>
    /// Общие настройки программы (для всех копий): %LOCALAPPDATA%\OsEngineUpdater\settings.json.
    /// </summary>
    public sealed class UserSettings
    {
        public List<SourceEntry> Entries { get; set; } = new List<SourceEntry>();

        /// <summary>Как часто окно проверяет GitHub на новые коммиты для всех копий (минуты, 0 — не проверять).</summary>
        public int WatchMinutes { get; set; } = 10;

        /// <summary>Формат прошлой версии (отдельные репозитории с типом) — переносится в Entries.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LegacySource> Sources { get; set; }

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
                        s.Entries ??= new List<SourceEntry>();

                        if (s.Sources != null)
                        {
                            foreach (LegacySource old in s.Sources.Where(o => !string.IsNullOrWhiteSpace(o.Url)))
                            {
                                if (!s.Entries.Any(e => string.Equals(e.Url.TrimEnd('/'), old.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                                {
                                    s.Entries.Add(new SourceEntry { Url = old.Url.Trim(), Branch = old.Branch ?? "" });
                                }
                            }

                            s.Sources = null;
                        }

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

    /// <summary>Запись из прошлой версии настроек (только для переноса).</summary>
    public sealed class LegacySource
    {
        public string Url { get; set; }
        public string Branch { get; set; }
    }
}
