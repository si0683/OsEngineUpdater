using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OsEngineUpdater.Core
{
    /// <summary>Один репозиторий из ваших аккаунтов или отдельной записи.</summary>
    public sealed class RepoInfo
    {
        public string Name { get; init; }
        public string Owner { get; init; }
        public string CloneUrl { get; init; }
        public string WebUrl { get; init; }
        public string Branch { get; init; }
        public bool Private { get; init; }
        public string Description { get; init; }
        public GitProvider Provider { get; init; }

        /// <summary>Запись настроек, из которой взят репозиторий (для токена).</summary>
        public SourceEntry Entry { get; init; }

        /// <summary>Ключ репозитория: нормализованный адрес клонирования.</summary>
        public string Id => NormalizeId(CloneUrl);

        public static string NormalizeId(string url)
        {
            string s = (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            return s.EndsWith(".git") ? s.Substring(0, s.Length - 4) : s;
        }
    }

    /// <summary>
    /// Список репозиториев из «Мои аккаунты и репозитории»: аккаунты GitHub / GitFlic через их API,
    /// отдельные репозитории — как есть.
    /// </summary>
    public static class RepoCatalog
    {
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            HttpClient h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("OsEngine-Updater");
            return h;
        }

        public static async Task<List<RepoInfo>> ListAsync(SourceEntry entry, CancellationToken ct)
        {
            if (!entry.IsAccount)
            {
                return new List<RepoInfo> { SingleRepo(entry) };
            }

            return entry.Provider == GitProvider.GitHub
                ? await ListGitHubAsync(entry, ct)
                : await ListGitFlicAsync(entry, ct);
        }

        private static RepoInfo SingleRepo(SourceEntry e)
        {
            (GitProvider p, string owner, string repo) = SourceEntry.Parse(e.Url);
            string url = e.Url.Trim().TrimEnd('/');

            if (p == GitProvider.GitFlic && !url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                url += ".git";
            }

            return new RepoInfo
            {
                Name = repo ?? url,
                Owner = owner,
                CloneUrl = url,
                WebUrl = e.Url.Trim(),
                Branch = string.IsNullOrWhiteSpace(e.Branch) ? null : e.Branch.Trim(),
                Provider = p,
                Entry = e
            };
        }

        #region GitHub

        private static async Task<List<RepoInfo>> ListGitHubAsync(SourceEntry e, CancellationToken ct)
        {
            string token = e.Token;
            List<RepoInfo> result = new List<RepoInfo>();

            if (token.Length > 0)
            {
                // с токеном видны и закрытые: берём репозитории, где владелец — этот аккаунт
                foreach (JsonElement r in await GitHubPagesAsync("user/repos?affiliation=owner,organization_member,collaborator", token, ct))
                {
                    if (string.Equals(r.GetProperty("owner").GetProperty("login").GetString(), e.Owner, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(GitHubRepo(r, e));
                    }
                }
            }

            if (result.Count == 0)
            {
                // без токена (или аккаунт — чужой / организация): публичные репозитории
                foreach (JsonElement r in await GitHubPagesAsync($"users/{Uri.EscapeDataString(e.Owner)}/repos?type=owner", token, ct))
                {
                    result.Add(GitHubRepo(r, e));
                }
            }

            return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static RepoInfo GitHubRepo(JsonElement r, SourceEntry e)
        {
            return new RepoInfo
            {
                Name = r.GetProperty("name").GetString(),
                Owner = r.GetProperty("owner").GetProperty("login").GetString(),
                CloneUrl = r.GetProperty("clone_url").GetString(),
                WebUrl = r.GetProperty("html_url").GetString(),
                Private = r.TryGetProperty("private", out JsonElement p) && p.ValueKind == JsonValueKind.True,
                Description = Str(r, "description"),
                Provider = GitProvider.GitHub,
                Entry = e
            };
        }

        private static async Task<List<JsonElement>> GitHubPagesAsync(string path, string token, CancellationToken ct)
        {
            List<JsonElement> all = new List<JsonElement>();

            for (int page = 1; page <= 20; page++)
            {
                string sep = path.Contains('?') ? "&" : "?";
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/{path}{sep}per_page=100&page={page}");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                if (token.Length > 0)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                using HttpResponseMessage resp = await Http.SendAsync(req, ct);
                await EnsureOk(resp, "GitHub", ct);
                JsonElement arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
                int n = 0;

                foreach (JsonElement r in arr.EnumerateArray())
                {
                    all.Add(r.Clone());
                    n++;
                }

                if (n < 100)
                {
                    break;
                }
            }

            return all;
        }

        #endregion

        #region GitFlic

        private static async Task<List<RepoInfo>> ListGitFlicAsync(SourceEntry e, CancellationToken ct)
        {
            string token = e.Token;

            if (token.Length == 0)
            {
                throw new InvalidOperationException("Для аккаунта GitFlic нужен токен: GitFlic → профиль → «API токены» → «Создать». Вставьте его в «Мои аккаунты и репозитории».");
            }

            List<RepoInfo> result = new List<RepoInfo>();

            foreach (string kind in new[] { "my", "shared" })
            {
                for (int page = 0; page < 20; page++)
                {
                    using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://api.gitflic.ru/project/{kind}?page={page}&size=100");
                    req.Headers.TryAddWithoutValidation("Authorization", "token " + token);
                    using HttpResponseMessage resp = await Http.SendAsync(req, ct);

                    if (kind == "shared" && !resp.IsSuccessStatusCode)
                    {
                        break;
                    }

                    await EnsureOk(resp, "GitFlic", ct);
                    JsonElement root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;

                    if (!root.TryGetProperty("_embedded", out JsonElement emb) || !emb.TryGetProperty("projectList", out JsonElement list))
                    {
                        break;
                    }

                    foreach (JsonElement p in list.EnumerateArray())
                    {
                        string owner = p.TryGetProperty("owner", out JsonElement o) ? Str(o, "alias") : null;

                        if (owner != null && !string.Equals(owner, e.Owner, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string alias = Str(p, "alias");
                        owner ??= e.Owner;

                        if (result.Any(r => r.Name == alias && r.Owner == owner))
                        {
                            continue;
                        }

                        result.Add(new RepoInfo
                        {
                            Name = alias,
                            Owner = owner,
                            CloneUrl = Str(p, "httpTransportUrl") ?? $"https://gitflic.ru/project/{owner}/{alias}.git",
                            WebUrl = $"https://gitflic.ru/project/{owner}/{alias}",
                            Private = p.TryGetProperty("private", out JsonElement pr) && pr.ValueKind == JsonValueKind.True,
                            Description = Str(p, "description") ?? Str(p, "title"),
                            Provider = GitProvider.GitFlic,
                            Entry = e
                        });
                    }

                    int totalPages = root.TryGetProperty("page", out JsonElement pg) && pg.TryGetProperty("totalPages", out JsonElement tp) ? tp.GetInt32() : 1;

                    if (page + 1 >= totalPages)
                    {
                        break;
                    }
                }
            }

            return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        #endregion

        /// <summary>
        /// Заголовок авторизации git для токена записи (передаётся через переменные окружения, не в командной строке).
        /// </summary>
        public static string GitAuthHeader(RepoInfo repo)
        {
            string token = repo.Entry?.Token ?? "";

            if (token.Length == 0)
            {
                return null;
            }

            string user = repo.Provider == GitProvider.GitHub ? "x-access-token" : (repo.Entry.Owner ?? "oauth2");
            return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + token));
        }

        private static async Task EnsureOk(HttpResponseMessage resp, string host, CancellationToken ct)
        {
            if (resp.IsSuccessStatusCode)
            {
                return;
            }

            string msg = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => $"{host} отклонил токен (401). Проверьте токен и срок его действия.",
                HttpStatusCode.Forbidden => $"{host}: доступ запрещён (403) — нет токена, у токена мало прав или исчерпан лимит запросов.",
                HttpStatusCode.NotFound => $"{host}: аккаунт не найден (404). Проверьте адрес.",
                _ => $"{host}: HTTP {(int)resp.StatusCode}"
            };

            throw new InvalidOperationException(msg);
        }

        private static string Str(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }
}
