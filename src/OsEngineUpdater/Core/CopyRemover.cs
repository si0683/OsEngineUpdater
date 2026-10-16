using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Удаление копии OsEngine с диска: проверки, выключение автообновления, папка — в Корзину.
    /// </summary>
    public static class CopyRemover
    {
        /// <summary>Причины, по которым удалять нельзя. Пустой список — можно.</summary>
        public static List<string> FindBlockers(CopyInfo copy, IEnumerable<CopyInfo> otherCopies)
        {
            List<string> blockers = new List<string>();
            string prefix = copy.Root.TrimEnd('\\') + "\\";

            if (IsUnder(Environment.ProcessPath, prefix))
            {
                blockers.Add("OsEngineUpdater.exe запущен из этой копии. Перенесите exe в другую папку "
                             + "(например, D:\\Tools), запустите оттуда и удалите копию.");
            }

            foreach (string name in RunningFrom(prefix))
            {
                blockers.Add($"Запущена программа из этой копии: {name}. Закройте её.");
            }

            foreach (CopyInfo other in otherCopies)
            {
                AutoTaskInfo t = AutoTaskService.Query(other.TaskName);

                if (t.Installed && IsUnder(t.Command, prefix))
                {
                    blockers.Add($"Автообновление копии «{other.Name}» запускает exe из этой копии. "
                                 + "Переместите OsEngineUpdater.exe и включите авто для неё заново.");
                }
            }

            return blockers;
        }

        /// <summary>Размер папки в байтах (для подтверждения).</summary>
        public static long GetSize(string dir, CancellationToken ct)
        {
            long size = 0;
            EnumerationOptions opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

            foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", opt))
            {
                ct.ThrowIfCancellationRequested();
                size += f.Length;
            }

            return size;
        }

        /// <summary>
        /// Выключает автообновление и отправляет папку копии в Корзину (со стандартными окнами Windows).
        /// </summary>
        /// <returns>true — папки больше нет; false — удаление отменено или не завершено.</returns>
        public static bool Delete(CopyInfo copy, bool showDialogs)
        {
            AutoTaskService.Remove(copy.TaskName);

            try
            {
                FileSystem.DeleteDirectory(copy.Root,
                    showDialogs ? UIOption.AllDialogs : UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.DoNothing);
            }
            catch (OperationCanceledException)
            {
                // пользователь отменил в окне Windows
            }

            return !Directory.Exists(copy.Root);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} ГБ";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} МБ";
            return $"{Math.Max(1, bytes / 1024)} КБ";
        }

        private static List<string> RunningFrom(string prefix)
        {
            List<string> names = new List<string>();
            int self = Environment.ProcessId;

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && IsUnder(p.MainModule?.FileName, prefix))
                    {
                        names.Add(p.ProcessName + ".exe");
                    }
                }
                catch
                {
                    // системные процессы без доступа — не наши
                }
                finally
                {
                    p.Dispose();
                }
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsUnder(string path, string prefix)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
