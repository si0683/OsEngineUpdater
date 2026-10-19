using System.Runtime.InteropServices;
using System.Text;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Командная строка:
    ///   OsEngineUpdater.exe --update &lt;копия&gt; [--mode mirror|delta|full|check] [--silent] [--force] [--no-build]
    ///   OsEngineUpdater.exe --auto-on &lt;копия&gt; | --auto-off &lt;копия&gt; | --status &lt;копия&gt;
    ///   OsEngineUpdater.exe --new-copy &lt;папка&gt;
    /// Без аргументов открывается окно.
    /// </summary>
    public static class Cli
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private static bool _silent;

        public static int Run(string[] args)
        {
            _silent = Has(args, "--silent");

            if (!_silent && AttachConsole(-1))
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                Console.WriteLine();
            }

            try
            {
                if (Value(args, "--update") is string updRoot)
                {
                    return Task.Run(() => UpdateAsync(updRoot, args)).GetAwaiter().GetResult();
                }

                if (Value(args, "--auto-on") is string onRoot)
                {
                    CopyInfo copy = ResolveCopy(onRoot);
                    UpdaterConfig cfg = UpdaterConfig.Load(copy);
                    int minutes = UserSettings.Load().EffectiveAutoMinutes;
                    AutoTaskService.Install(copy, cfg, Environment.ProcessPath, minutes);
                    Print(LogLevel.Ok, $"Автообновление включено: {copy.TaskName} ({cfg.AutoMode}), {UserSettings.EveryText(minutes)} и при входе в систему.");
                    return ExitCodes.Ok;
                }

                if (Value(args, "--auto-off") is string offRoot)
                {
                    CopyInfo copy = ResolveCopy(offRoot);
                    AutoTaskService.Remove(copy.TaskName);
                    Print(LogLevel.Ok, $"Автообновление выключено: {copy.TaskName} удалено.");
                    return ExitCodes.Ok;
                }

                if (Value(args, "--status") is string stRoot)
                {
                    CopyInfo copy = ResolveCopy(stRoot);
                    AutoTaskInfo t = AutoTaskService.Query(copy.TaskName);

                    if (!t.Installed)
                    {
                        Print(LogLevel.Warn, $"Автообновление выключено ({copy.TaskName} не установлено).");
                        return ExitCodes.Ok;
                    }

                    Print(LogLevel.Info, $"Задание  : {copy.TaskName}{(t.Legacy ? " (старый .ps1)" : "")}");
                    Print(LogLevel.Info, $"Состояние: {t.State}");
                    Print(LogLevel.Info, t.LastRun is DateTime lr ? $"Последний: {lr:dd.MM.yyyy HH:mm} ({t.LastResultText})" : "Последний: ещё не запускалось");
                    Print(LogLevel.Info, t.NextRun is DateTime nr ? $"Следующий: {nr:dd.MM.yyyy HH:mm}" : "Следующий: при входе в систему");
                    Print(LogLevel.Info, $"Команда  : {t.Command} {t.Arguments}");
                    return ExitCodes.Ok;
                }

                if (Value(args, "--new-copy") is string target)
                {
                    UpdaterConfig cfg = new UpdaterConfig();
                    return Task.Run(() => UpdateEngine.BootstrapAsync(CopyInfo.NormalizeRoot(target), cfg, Print, CancellationToken.None))
                        .GetAwaiter().GetResult();
                }

                if (Has(args, "--version"))
                {
                    Print(LogLevel.Info, SelfUpdater.CurrentVersionText);
                    return ExitCodes.Ok;
                }

                if (Has(args, "--self-update"))
                {
                    return Task.Run(SelfUpdateNowAsync).GetAwaiter().GetResult();
                }

                PrintHelp();
                return ExitCodes.Error;
            }
            catch (Exception ex)
            {
                Print(LogLevel.Error, "Ошибка: " + ex.Message);
                return ExitCodes.Error;
            }
        }

        private static async Task<int> UpdateAsync(string rootArg, string[] args)
        {
            CopyInfo copy = ResolveCopy(rootArg);
            UpdaterConfig cfg = UpdaterConfig.Load(copy);

            UpdateMode mode = (Value(args, "--mode") ?? "mirror").ToLowerInvariant() switch
            {
                "delta" => UpdateMode.Delta,
                "full" => UpdateMode.Full,
                "check" => UpdateMode.Check,
                _ => UpdateMode.Mirror
            };

            UpdateLog log = new UpdateLog(copy.LogPath, Print);

            using UpdateEngine engine = new UpdateEngine(copy, cfg, log)
            {
                Force = Has(args, "--force"),
                NoBuild = Has(args, "--no-build"),
                QuickIfUpToDate = Has(args, "--quick")
            };

            int code = await engine.RunAsync(mode, CancellationToken.None);

            // запуск из Планировщика заодно обновляет и саму программу (если пора и вышла новая версия)
            await SelfUpdater.TrySilentUpdateAsync(msg => log.Info(msg));
            return code;
        }

        /// <summary>--self-update: проверить новую версию программы и установить сразу.</summary>
        private static async Task<int> SelfUpdateNowAsync()
        {
            UserSettings s = UserSettings.Load();
            Print(LogLevel.Info, $"Установлена версия {SelfUpdater.CurrentVersionText}, источник github.com/{s.SelfUpdateRepo}");
            ReleaseInfo r = await SelfUpdater.GetLatestAsync(s.SelfUpdateRepo, CancellationToken.None);
            SelfUpdater.MarkChecked();

            if (!SelfUpdater.IsNewer(r))
            {
                Print(LogLevel.Ok, r == null ? "Релизов пока нет." : $"Установлена последняя версия (релиз {SelfUpdater.Short(r.Version)}).");
                return ExitCodes.Ok;
            }

            (bool can, string why) = SelfUpdater.CanSelfUpdate();

            if (!can)
            {
                Print(LogLevel.Error, $"Есть версия {SelfUpdater.Short(r.Version)}, но обновиться нельзя: {why}");
                return ExitCodes.Error;
            }

            await SelfUpdater.DownloadAndReplaceAsync(r, m => Print(LogLevel.Info, m), CancellationToken.None);
            return ExitCodes.Ok;
        }

        private static CopyInfo ResolveCopy(string path)
        {
            string root = CopyInfo.FindRoot(path)
                          ?? throw new DirectoryNotFoundException($"Не найдена копия OsEngine (project\\OsEngine\\OsEngine.csproj) в {path}");
            return new CopyInfo(root);
        }

        private static void Print(LogLevel level, string message)
        {
            if (_silent)
            {
                return;
            }

            try
            {
                Console.WriteLine(message);
            }
            catch
            {
                // нет консоли
            }
        }

        private static void PrintHelp()
        {
            Print(LogLevel.Info, "OsEngine Updater — без аргументов открывается окно.");
            Print(LogLevel.Info, "  --update <копия> [--mode mirror|delta|full|check] [--quick] [--silent] [--force] [--no-build]");
            Print(LogLevel.Info, "  --auto-on <копия> | --auto-off <копия> | --status <копия>");
            Print(LogLevel.Info, "  --new-copy <папка>");
            Print(LogLevel.Info, "  --version | --self-update");
        }

        private static bool Has(string[] args, string key)
        {
            return args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
        }

        private static string Value(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
