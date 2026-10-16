using System.Diagnostics;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Заставляет Visual Studio увидеть файлы/папки, добавленные в Robots/Indicators из проводника:
    /// «трогает» OsEngine.csproj и OsEngine.sln и чистит кэш .vs (если VS закрыта).
    /// Проект SDK-стиля, поэтому в сборку такие файлы попадают и без этого.
    /// </summary>
    public static class VsRefresher
    {
        public static void Run(CopyInfo copy, UpdateLog log)
        {
            string projectDir = Path.Combine(copy.Root, "project");
            string proj = Path.Combine(projectDir, @"OsEngine\OsEngine.csproj");
            string sln = Path.Combine(projectDir, "OsEngine.sln");

            foreach (string f in new[] { proj, sln })
            {
                if (File.Exists(f))
                {
                    File.SetLastWriteTime(f, DateTime.Now);
                    log.Info("Обновлена дата: " + f);
                }
            }

            Process[] devenv = Process.GetProcessesByName("devenv");
            bool vsRunning = devenv.Length > 0;

            foreach (Process p in devenv)
            {
                p.Dispose();
            }

            if (vsRunning)
            {
                log.Warn("Visual Studio запущена — кэш .vs не очищен. В VS: правый клик по проекту OsEngine → Reload Project.");
                return;
            }

            foreach (string d in new[] { Path.Combine(projectDir, ".vs"), Path.Combine(projectDir, @"OsEngine\.vs") })
            {
                if (!Directory.Exists(d))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(d, true);
                    log.Info("Очищен кэш VS: " + d);
                }
                catch (Exception ex)
                {
                    log.Warn($"Не удалось удалить {d}: {ex.Message}");
                }
            }

            log.Ok("Готово. Откройте project\\OsEngine.sln — файлы из Robots/Indicators будут видны.");
        }
    }
}
