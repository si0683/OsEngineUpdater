using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OsEngineUpdater.Core
{
    public sealed class TreeEntry
    {
        public string Path { get; init; }
        public string Sha { get; init; }
    }

    public sealed class ChangedFile
    {
        public string FileName { get; init; }
        public string Status { get; init; }
        public string PreviousFileName { get; init; }
    }

    public sealed class CompareResult
    {
        public int AheadBy { get; init; }
        public List<ChangedFile> Files { get; init; }
    }

    public sealed class GitHubException : Exception
    {
        public GitHubException(string message) : base(message) { }
    }

    /// <summary>
    /// Минимальный клиент GitHub: API (дерево, сравнение, коммиты) и скачивание raw-файлов / архива.
    /// </summary>
    public sealed class GitHubClient : IDisposable
    {
        private const string UserAgent = "OsEngine-Updater";

        private readonly HttpClient _api;
        private readonly HttpClient _raw;
        private readonly string _owner;
        private readonly string _repo;
        private readonly string _branch;

        public GitHubClient(UpdaterConfig cfg)
        {
            _owner = cfg.Owner;
            _repo = cfg.Repo;
            _branch = cfg.Branch;

            _api = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
            _api.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            if (!string.IsNullOrWhiteSpace(cfg.GitHubToken))
            {
                _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.GitHubToken.Trim());
            }

            _raw = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _raw.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        public string Source => $"{_owner}/{_repo}@{_branch}";

        public async Task<string> GetHeadShaAsync(CancellationToken ct)
        {
            using JsonDocument doc = await ApiAsync($"repos/{_owner}/{_repo}/commits/{Uri.EscapeDataString(_branch)}", ct);
            return doc.RootElement.GetProperty("sha").GetString();
        }

        public async Task<DateTime?> GetCommitDateUtcAsync(string sha, CancellationToken ct)
        {
            using JsonDocument doc = await ApiAsync($"repos/{_owner}/{_repo}/commits/{sha}", ct);

            if (doc.RootElement.TryGetProperty("commit", out JsonElement commit)
                && commit.TryGetProperty("committer", out JsonElement committer)
                && committer.TryGetProperty("date", out JsonElement date)
                && date.TryGetDateTime(out DateTime dt))
            {
                return dt.ToUniversalTime();
            }

            return null;
        }

        /// <summary>Все файлы (blob) репозитория на коммите. truncated — GitHub отдал неполное дерево.</summary>
        public async Task<(List<TreeEntry> Blobs, bool Truncated)> GetTreeAsync(string sha, CancellationToken ct)
        {
            using JsonDocument doc = await ApiAsync($"repos/{_owner}/{_repo}/git/trees/{sha}?recursive=1", ct);
            JsonElement root = doc.RootElement;
            bool truncated = root.TryGetProperty("truncated", out JsonElement t) && t.ValueKind == JsonValueKind.True;
            List<TreeEntry> blobs = new List<TreeEntry>();

            foreach (JsonElement e in root.GetProperty("tree").EnumerateArray())
            {
                if (e.GetProperty("type").GetString() == "blob")
                {
                    blobs.Add(new TreeEntry { Path = e.GetProperty("path").GetString(), Sha = e.GetProperty("sha").GetString() });
                }
            }

            return (blobs, truncated);
        }

        public async Task<CompareResult> CompareAsync(string fromSha, string toSha, CancellationToken ct)
        {
            using JsonDocument doc = await ApiAsync($"repos/{_owner}/{_repo}/compare/{fromSha}...{toSha}", ct);
            JsonElement root = doc.RootElement;
            List<ChangedFile> files = new List<ChangedFile>();

            if (root.TryGetProperty("files", out JsonElement arr))
            {
                foreach (JsonElement f in arr.EnumerateArray())
                {
                    files.Add(new ChangedFile
                    {
                        FileName = f.GetProperty("filename").GetString(),
                        Status = f.GetProperty("status").GetString(),
                        PreviousFileName = f.TryGetProperty("previous_filename", out JsonElement p) ? p.GetString() : null
                    });
                }
            }

            return new CompareResult
            {
                AheadBy = root.TryGetProperty("ahead_by", out JsonElement a) ? a.GetInt32() : 0,
                Files = files
            };
        }

        public async Task<byte[]> DownloadFileAsync(string path, string sha, CancellationToken ct)
        {
            string url = $"https://raw.githubusercontent.com/{_owner}/{_repo}/{sha}/"
                         + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

            using HttpResponseMessage resp = await _raw.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                throw new GitHubException($"HTTP {(int)resp.StatusCode} при скачивании {path}");
            }

            return await resp.Content.ReadAsByteArrayAsync(ct);
        }

        public async Task DownloadArchiveAsync(string destZip, CancellationToken ct)
        {
            string url = $"https://codeload.github.com/{_owner}/{_repo}/zip/refs/heads/{Uri.EscapeDataString(_branch)}";
            using HttpResponseMessage resp = await _raw.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                throw new GitHubException($"HTTP {(int)resp.StatusCode} при скачивании архива");
            }

            await using FileStream fs = File.Create(destZip);
            await resp.Content.CopyToAsync(fs, ct);
        }

        private async Task<JsonDocument> ApiAsync(string relativeUrl, CancellationToken ct)
        {
            using HttpResponseMessage resp = await _api.GetAsync(relativeUrl, ct);

            if (resp.IsSuccessStatusCode)
            {
                await using Stream s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(s, cancellationToken: ct);
            }

            if ((resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                && resp.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string> rem)
                && rem.FirstOrDefault() == "0")
            {
                string when = "";

                if (resp.Headers.TryGetValues("x-ratelimit-reset", out IEnumerable<string> reset)
                    && long.TryParse(reset.FirstOrDefault(), out long unix))
                {
                    when = $" Лимит восстановится в {DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime():HH:mm}.";
                }

                throw new GitHubException("Исчерпан лимит GitHub API (без токена — 60 запросов в час)."
                                          + when + " Укажите токен GitHub в настройках.");
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new GitHubException("GitHub отклонил токен (401). Проверьте токен в настройках.");
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new GitHubException($"GitHub: не найдено ({relativeUrl}). Проверьте Owner/Repo/Branch.");
            }

            throw new GitHubException($"GitHub API: HTTP {(int)resp.StatusCode} ({relativeUrl})");
        }

        public void Dispose()
        {
            _api.Dispose();
            _raw.Dispose();
        }
    }
}
