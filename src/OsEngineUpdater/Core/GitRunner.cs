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

        public static async Task<(int Code, string Out, string Err)> RunAsync(string workDir, CancellationToken ct, params string[] args)
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
        public static async Task<string> RemoteHeadAsync(PackageSource src, CancellationToken ct)
        {
            string reference = string.IsNullOrWhiteSpace(src.Branch) ? "HEAD" : "refs/heads/" + src.Branch.Trim();
            (int code, string output, string err) = await RunAsync(null, ct, "ls-remote", src.CloneUrl, reference);

            if (code != 0)
            {
                throw new InvalidOperationException(ShortError(err));
            }

            string line = output.Split('\n').FirstOrDefault(l => l.Length >= 40);

            if (line == null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(src.Branch)
                    ? "Репозиторий пуст."
                    : $"Ветка {src.Branch} не найдена.");
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
