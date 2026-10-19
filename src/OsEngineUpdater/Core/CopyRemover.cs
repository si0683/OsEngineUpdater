using Microsoft.VisualBasic.FileIO;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Удаление копии OsEngine с диска: проверки, закрытие программ, которые держат её файлы,
    /// выключение автообновления, папка — в Корзину.
    /// </summary>
    public static class CopyRemover
    {
        /// <summary>
        /// Причины, по которым удалять нельзя (их программа сама не исправит). Пустой список — можно.
        /// Программы, держащие файлы копии, сюда не входят — их программа закроет сама.
        /// </summary>
        public static List<string> FindBlockers(CopyInfo copy, IEnumerable<CopyInfo> otherCopies)
        {
            List<string> blockers = new List<string>();
            string prefix = copy.Root.TrimEnd('\\') + "\\";

            if (IsUnder(Environment.ProcessPath, prefix))
            {
                blockers.Add("OsEngineUpdater.exe запущен из этой копии. Перенесите exe в другую папку "
                             + "(например, D:\\Tools), запустите оттуда и удалите копию.");
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
        /// Закрыть всё, что держит файлы копии, выключить автообновление и отправить папку в Корзину.
        /// </summary>
        /// <returns>null — папка удалена; иначе текст, почему не получилось.</returns>
        public static string Delete(CopyInfo copy, Action<string> log)
        {
            AutoTaskService.Remove(copy.TaskName);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                List<LockingProcess> lockers = FileLockers.Find(copy.Root, CancellationToken.None);

                if (lockers.Count > 0)
                {
                    List<LockingProcess> left = FileLockers.Close(lockers, log);

                    if (left.Count > 0)
                    {
                        return "Не удалось закрыть: " + string.Join(", ", left.Select(x => x.Title))
                               + ". Закройте их вручную и повторите.";
                    }

                    // процессы отпускают файлы не мгновенно
                    Thread.Sleep(1000);
                }

                try
                {
                    // без окон Windows: в Корзину; если не помещается в Корзину — Windows спросит сама
                    FileSystem.DeleteDirectory(copy.Root, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                }
                catch (Exception ex) when (attempt == 1)
                {
                    log?.Invoke("Первая попытка не удалась: " + ex.Message);
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }

                if (!Directory.Exists(copy.Root))
                {
                    return null;
                }
            }

            return "Папка удалена не полностью. Возможно, она открыта в терминале или другой программе, которую Windows не показывает. "
                   + "Закройте её и удалите остаток вручную: " + copy.Root;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} ГБ";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} МБ";
            return $"{Math.Max(1, bytes / 1024)} КБ";
        }

        private static bool IsUnder(string path, string prefix)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
