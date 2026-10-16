using System.Diagnostics;
using System.Text;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Запуск установленного git. Закрытые репозитории: логин спрашивает Git Credential Manager
    /// (идёт вместе с Git for Windows) один раз и запоминает.
    /// </summary>
    public static class GitRunner
    {
        private static readonly Lazy<string> GitPathLazy = new Lazy<string>(Find);

        public static string GitPath => GitPathLazy.Value;

        public static bool Available => GitPath != null;

        private static string Find()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string p = Path.Combine(dir.Trim().Trim('"'), "git.exe");

                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
                catch
                {
                    // кривой элемент PATH
                }
            }

            foreach (string p in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\cmd\git.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe")
                     })
            {
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return null;
        }

        public static Task<(int Code, string Out, string Err)> RunAsync(string workDir, CancellationToken ct, params string[] args)
        {
            return RunWithAuthAsync(workDir, null, ct, args);
        }

        /// <param name="authHeader">HTTP-заголовок авторизации (токен) — передаётся через GIT_CONFIG_*, не виден в командной строке.</param>
        public static async Task<(int Code, string Out, string Err)> RunWithAuthAsync(string workDir, string authHeader, CancellationToken ct, params string[] args)
        {
            if (!Available)
            {
                throw new InvalidOperationException("Git не найден. Установите Git for Windows (git-scm.com) и перезапустите программу.");
            }

            ProcessStartInfo psi = new ProcessStartInfo(GitPath)
            {
                WorkingDirectory = workDir ?? Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            psi.Environment["LC_ALL"] = "C";

            // окно входа Git Credential Manager должно появляться, даже если запрет унаследован из окружения
            psi.Environment.Remove("GIT_TERMINAL_PROMPT");
            psi.Environment.Remove("GCM_INTERACTIVE");

            // длинные пути: без core.longpaths git для Windows падает на путях > 260 символов
            // (например, «invalid index-pack output» при клонировании в глубокую папку)
            psi.Environment["GIT_CONFIG_COUNT"] = string.IsNullOrEmpty(authHeader) ? "1" : "2";
            psi.Environment["GIT_CONFIG_KEY_0"] = "core.longpaths";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "true";

            if (!string.IsNullOrEmpty(authHeader))
            {
                psi.Environment["GIT_CONFIG_KEY_1"] = "http.extraHeader";
                psi.Environment["GIT_CONFIG_VALUE_1"] = authHeader;
            }

            using Process p = new Process { StartInfo = psi };
            p.Start();
            p.StandardInput.Close();

            Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = p.StandardError.ReadToEndAsync(ct);

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }

            return (p.ExitCode, (await stdout).Trim(), (await stderr).Trim());
        }

        /// <summary>SHA последнего коммита ветки на сервере (git ls-remote), без скачивания.</summary>
        public static async Task<string> RemoteHeadAsync(RepoInfo repo, CancellationToken ct)
        {
            string reference = string.IsNullOrWhiteSpace(repo.Branch) ? "HEAD" : "refs/heads/" + repo.Branch.Trim();
            (int code, string output, string err) = await RunWithAuthAsync(null, RepoCatalog.GitAuthHeader(repo), ct, "ls-remote", repo.CloneUrl, reference);

            if (code != 0)
            {
                throw new InvalidOperationException(ShortError(err));
            }

            string line = output.Split('\n').FirstOrDefault(l => l.Length >= 40);

            if (line == null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(repo.Branch)
                    ? "Репозиторий пуст."
                    : $"Ветка {repo.Branch} не найдена.");
            }

            return line.Substring(0, 40);
        }

        public static string ShortError(string err)
        {
            string line = (err ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "ошибка git";

            if (line.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Нет доступа: репозиторий закрытый, а вход в Git не выполнен (или отменён).";
            }

            if (line.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return "Репозиторий не найден (или закрыт). Проверьте адрес.";
            }

            return line.StartsWith("fatal: ") ? line.Substring(7) : line;
        }
    }
}
