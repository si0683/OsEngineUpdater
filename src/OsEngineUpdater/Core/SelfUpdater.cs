using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace OsEngineUpdater.Core
{
    /// <summary>Последний релиз программы на GitHub.</summary>
    public sealed class ReleaseInfo
    {
        public Version Version { get; init; }
        public string Tag { get; init; }
        public string ExeUrl { get; init; }
        public long ExeSize { get; init; }

        /// <summary>SHA-256 exe (из файла .sha256 релиза или из поля digest ассета).</summary>
        public string Sha256 { get; init; }

        public string Notes { get; init; }
        public string HtmlUrl { get; init; }
    }

    /// <summary>
    /// Обновление самой программы из GitHub Releases её репозитория.
    /// Скачивается только exe из релиза по HTTPS, проверяется SHA-256, затем текущий exe подменяется:
    /// работающий файл переименовывается (Windows это позволяет), новый встаёт на его место.
    /// Обновление идёт только на более новую версию — свою сборку разработчика программа не откатывает.
    /// </summary>
    public static class SelfUpdater
    {
        public const string DefaultRepo = "si0683/OsEngineUpdater";
        public const string ExeName = "OsEngineUpdater.exe";

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            HttpClient h = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("OsEngine-Updater/" + CurrentVersion);
            return h;
        }

        /// <summary>Версия запущенной программы (из сборки; при выпуске её задаёт тег релиза).</summary>
        public static Version CurrentVersion
        {
            get
            {
                string info = typeof(SelfUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                return ParseVersion(info) ?? typeof(SelfUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            }
        }

        public static string CurrentVersionText => Short(CurrentVersion);

        public static string Short(Version v) => v == null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

        /// <summary>«v2.1.0», «2.1.0+abc», «2.1» → Version.</summary>
        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string s = text.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '+', '-', ' ' });

            if (cut >= 0)
            {
                s = s.Substring(0, cut);
            }

            return Version.TryParse(s, out Version v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
        }

        /// <summary>
        /// Можно ли подменять exe: программа запущена не из папки сборки (bin\Debug, bin\Release — это разработка)
        /// и папка с exe доступна на запись.
        /// </summary>
        public static (bool Can, string Why) CanSelfUpdate()
        {
            string exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "программа запущена не из OsEngineUpdater.exe");
            }

            string dir = Path.GetDirectoryName(exe);

            if (dir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "запущена сборка разработчика (папка bin) — она себя не обновляет");
            }

            try
            {
                string probe = Path.Combine(dir, ".osu_write_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch
            {
                return (false, $"нет прав на запись в папку {dir} — перенесите программу в папку пользователя (например, D:\\Tools)");
            }

            return (true, null);
        }

        /// <summary>Последний релиз репозитория программы (один запрос к GitHub API).</summary>
        public static async Task<ReleaseInfo> GetLatestAsync(string repo, CancellationToken ct)
        {
            repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim().Trim('/');

            // для проверки обновления на тестовом релизе: адрес JSON в формате GitHub API
            string api = Environment.GetEnvironmentVariable("OSENGINEUPDATER_RELEASE_API");

            if (string.IsNullOrWhiteSpace(api))
            {
                api = $"https://api.github.com/repos/{repo}/releases/latest";
            }

            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, api);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null; // релизов ещё нет
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub: HTTP {(int)resp.StatusCode} при проверке новой версии программы");
            }

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            JsonElement r = doc.RootElement;

            if (r.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            string tag = r.GetProperty("tag_name").GetString();
            string exeUrl = null;
            string shaUrl = null;
            string digest = null;
            long size = 0;

            foreach (JsonElement a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString();

                if (string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : 0;

                    if (a.TryGetProperty("digest", out JsonElement dg) && dg.ValueKind == JsonValueKind.String
                        && dg.GetString().StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        digest = dg.GetString().Substring(7);
                    }
                }
                else if (string.Equals(name, ExeName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    shaUrl = a.GetProperty("browser_download_url").GetString();
                }
            }

            if (exeUrl == null)
            {
                return null; // в релизе нет exe
            }

            string sha = digest;

            if (shaUrl != null)
            {
                // «<hash>  OsEngineUpdater.exe»
                string text = await Http.GetStringAsync(shaUrl, ct);
                sha = text.Trim().Split(' ', '\t', '\r', '\n')[0];
            }

            return new ReleaseInfo
            {
                Version = ParseVersion(tag),
                Tag = tag,
                ExeUrl = exeUrl,
                ExeSize = size,
                Sha256 = sha?.Trim().ToLowerInvariant(),
                Notes = r.TryGetProperty("body", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "",
                HtmlUrl = r.TryGetProperty("html_url", out JsonElement h) ? h.GetString() : null
            };
        }

        public static bool IsNewer(ReleaseInfo r) => r?.Version != null && r.Version > CurrentVersion;

        /// <summary>
        /// Скачать exe релиза, проверить SHA-256 и подменить текущий exe.
        /// Возвращает путь к новому exe (тот же, что у текущего).
        /// </summary>
        public static async Task<string> DownloadAndReplaceAsync(ReleaseInfo r, Action<string> progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(r.Sha256) || r.Sha256.Length != 64)
            {
                throw new InvalidOperationException("в релизе нет контрольной суммы SHA-256 — обновление не установлено");
            }

            string exe = Environment.ProcessPath;
            string dir = Path.GetDirectoryName(exe);
            string download = Path.Combine(dir, ExeName + ".download");

            progress?.Invoke($"Скачиваю версию {Short(r.Version)}…");

            using (HttpResponseMessage resp = await Http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using FileStream fs = File.Create(download);
                await resp.Content.CopyToAsync(fs, ct);
            }

            string actual;

            await using (FileStream fs = File.OpenRead(download))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            }

            if (actual != r.Sha256)
            {
                TryDelete(download);
                throw new InvalidOperationException("контрольная сумма скачанного файла не совпала — обновление не установлено");
            }

            // работающий exe нельзя перезаписать, но можно переименовать
            string old = Path.Combine(dir, $"OsEngineUpdater.old-{DateTime.Now:yyyyMMddHHmmss}.exe");
            File.Move(exe, old);

            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(old, exe); // вернуть как было
                throw;
            }

            progress?.Invoke($"Программа обновлена до {Short(r.Version)}.");
            return exe;
        }

        /// <summary>Удалить старые exe, оставшиеся после обновления (если они уже не заняты).</summary>
        public static void CleanupOld()
        {
            try
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath);

                foreach (string f in Directory.EnumerateFiles(dir, "OsEngineUpdater.old-*.exe")
                             .Concat(Directory.EnumerateFiles(dir, ExeName + ".download")))
                {
                    TryDelete(f);
                }
            }
            catch
            {
                // уборка — не критично
            }
        }

        /// <summary>Пора ли проверять (по настройке частоты и времени прошлой проверки).</summary>
        public static bool IsDue(UserSettings s)
        {
            if (!s.SelfUpdateEnabled)
            {
                return false;
            }

            if (!DateTime.TryParse(s.LastSelfCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last))
            {
                return true;
            }

            return DateTime.UtcNow - last.ToUniversalTime() >= TimeSpan.FromMinutes(Math.Max(10, s.SelfUpdateMinutes));
        }

        /// <summary>Запомнить время проверки (перечитывая настройки, чтобы не затереть чужие изменения).</summary>
        public static void MarkChecked()
        {
            try
            {
                UserSettings s = UserSettings.Load();
                s.LastSelfCheckUtc = DateTime.UtcNow.ToString("o");
                s.Save();
            }
            catch
            {
                // не критично
            }
        }

        /// <summary>
        /// Тихое самообновление из командной строки (задание Планировщика): проверить, если пора, и подменить exe.
        /// Новая версия начнёт работать со следующего запуска. Несколько одновременных запусков не мешают друг другу.
        /// </summary>
        public static async Task TrySilentUpdateAsync(Action<string> log)
        {
            UserSettings s = UserSettings.Load();

            if (!IsDue(s) || !CanSelfUpdate().Can)
            {
                return;
            }

            using Semaphore guard = new Semaphore(1, 1, "Local\\OsEngineUpdater_SelfUpdate");

            if (!guard.WaitOne(0))
            {
                return;
            }

            try
            {
                MarkChecked();
                ReleaseInfo r = await GetLatestAsync(s.SelfUpdateRepo, CancellationToken.None);

                if (IsNewer(r))
                {
                    await DownloadAndReplaceAsync(r, log, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Обновление программы не удалось: " + ex.Message);
            }
            finally
            {
                guard.Release();
            }
        }

        /// <summary>Перезапустить окно новой версией.</summary>
        public static void Restart(string exe)
        {
            Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false });
        }

        private static void TryDelete(string f)
        {
            try
            {
                File.Delete(f);
            }
            catch
            {
                // занят — удалится в следующий раз
            }
        }
    }
}
