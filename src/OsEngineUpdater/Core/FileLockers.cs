using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OsEngineUpdater.Core
{
    /// <summary>Процесс, который держит файлы папки (или запущен из неё).</summary>
    public sealed class LockingProcess
    {
        public int Pid { get; init; }
        public string Name { get; init; }

        /// <summary>Понятное имя: «Visual Studio», «OsEngine» и т.п.</summary>
        public string Title { get; init; }

        public bool IsVisualStudio => string.Equals(Name, "devenv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Поиск процессов, которые держат файлы папки, через Windows Restart Manager
    /// (тот же механизм, которым пользуются установщики), и их закрытие.
    /// </summary>
    public static class FileLockers
    {
        private const int ChunkSize = 1000;
        private const int ErrorMoreData = 234;

        /// <summary>Системные процессы, которые нельзя закрывать (Проводник, службы, антивирус).</summary>
        private static readonly HashSet<string> NeverClose = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "svchost", "csrss", "lsass", "services", "wininit", "winlogon", "smss", "System",
            "MsMpEng", "MpDefenderCoreService", "NisSrv", "SecurityHealthService", "dwm", "sihost", "ctfmon"
        };

        /// <summary>Можно ли закрыть процесс программой (системные — нет).</summary>
        public static bool CanClose(LockingProcess lp) => !NeverClose.Contains(lp.Name);

        /// <summary>Процессы, держащие файлы папки или запущенные из неё (кроме самой программы).</summary>
        public static List<LockingProcess> Find(string root, CancellationToken ct)
        {
            Dictionary<int, LockingProcess> found = new Dictionary<int, LockingProcess>();
            int self = Environment.ProcessId;
            string prefix = root.TrimEnd('\\') + "\\";

            // 1) запущенные из папки (например, OsEngine.exe этой копии)
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && (p.MainModule?.FileName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        found[p.Id] = Describe(p.Id, p.ProcessName, null);
                    }
                }
                catch
                {
                    // нет доступа — не наш процесс
                }
                finally
                {
                    p.Dispose();
                }
            }

            // 2) держащие файлы (Visual Studio, сборка, git, антивирус и т.д.)
            EnumerationOptions opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            List<string> chunk = new List<string>(ChunkSize);

            foreach (string f in Directory.EnumerateFiles(root, "*", opt))
            {
                ct.ThrowIfCancellationRequested();
                chunk.Add(f);

                if (chunk.Count == ChunkSize)
                {
                    Query(chunk, found, self);
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                Query(chunk, found, self);
            }

            return found.Values.OrderBy(x => x.Title).ToList();
        }

        /// <summary>
        /// Закрыть процессы: Visual Studio — сначала обычной командой закрытия окна, остальные — сразу.
        /// Возвращает то, что закрыть не удалось.
        /// </summary>
        public static List<LockingProcess> Close(IEnumerable<LockingProcess> list, Action<string> log)
        {
            List<LockingProcess> failed = new List<LockingProcess>();

            foreach (LockingProcess lp in list)
            {
                if (!CanClose(lp))
                {
                    failed.Add(lp);
                    continue;
                }

                try
                {
                    using Process p = Process.GetProcessById(lp.Pid);

                    if (lp.IsVisualStudio && p.CloseMainWindow() && p.WaitForExit(5000))
                    {
                        log?.Invoke($"Закрыто: {lp.Title}");
                        continue;
                    }

                    p.Kill(true);
                    p.WaitForExit(5000);
                    log?.Invoke($"Завершено: {lp.Title}");
                }
                catch (ArgumentException)
                {
                    // процесс уже завершился
                }
                catch (Exception ex)
                {
                    failed.Add(lp);
                    log?.Invoke($"Не удалось закрыть {lp.Title}: {ex.Message}");
                }
            }

            return failed;
        }

        private static void Query(List<string> files, Dictionary<int, LockingProcess> found, int self)
        {
            StringBuilder key = new StringBuilder(33);

            if (RmStartSession(out uint session, 0, key) != 0)
            {
                return;
            }

            try
            {
                if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null) != 0)
                {
                    return;
                }

                uint needed = 0;
                uint count = 0;
                uint reasons = 0;
                int rc = RmGetList(session, out needed, ref count, null, ref reasons);

                if (rc != ErrorMoreData || needed == 0)
                {
                    return;
                }

                RmProcessInfo[] info = new RmProcessInfo[needed];
                count = needed;

                if (RmGetList(session, out needed, ref count, info, ref reasons) != 0)
                {
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    int pid = info[i].Process.dwProcessId;

                    if (pid == self || pid <= 4 || found.ContainsKey(pid))
                    {
                        continue;
                    }

                    string name;

                    try
                    {
                        using Process p = Process.GetProcessById(pid);
                        name = p.ProcessName;
                    }
                    catch
                    {
                        continue; // уже завершился
                    }

                    found[pid] = Describe(pid, name, info[i].strAppName);
                }
            }
            finally
            {
                RmEndSession(session);
            }
        }

        private static LockingProcess Describe(int pid, string name, string appName)
        {
            string title = name.ToLowerInvariant() switch
            {
                "devenv" => "Visual Studio",
                "osengine" => "OsEngine",
                "vbcscompiler" => "компилятор C# (VBCSCompiler)",
                "msbuild" => "MSBuild",
                "dotnet" => "dotnet (сборка)",
                "git" => "git",
                "explorer" => "Проводник",
                _ => string.IsNullOrWhiteSpace(appName) ? name : appName
            };

            return new LockingProcess { Pid = pid, Name = name, Title = $"{title} (PID {pid})" };
        }

        #region Restart Manager

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;

            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;

            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, RmUniqueProcess[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RmProcessInfo[] rgAffectedApps, ref uint lpdwRebootReasons);

        #endregion
    }
}
