using System.Runtime.InteropServices;
using System.Security;

namespace OsEngineUpdater.Core
{
    public sealed class AutoTaskInfo
    {
        public bool Installed { get; init; }
        public bool Enabled { get; init; }
        public string State { get; init; }
        public DateTime? LastRun { get; init; }
        public int LastResult { get; init; }
        public DateTime? NextRun { get; init; }

        /// <summary>Задание запускает старый UpdateOsEngine.ps1, а не exe.</summary>
        public bool Legacy { get; init; }

        public string Command { get; init; }
        public string Arguments { get; init; }

        public string LastResultText
        {
            get
            {
                if (LastRun == null) return "ещё не запускалось";

                return LastResult switch
                {
                    0 => "успешно",
                    ExitCodes.SkippedRunning => "пропущено: OsEngine был запущен",
                    ExitCodes.Busy => "пропущено: уже выполнялось",
                    0x41301 => "выполняется",
                    0x41303 => "ещё не запускалось",
                    _ => $"код {LastResult}"
                };
            }
        }
    }

    /// <summary>
    /// Задание автообновления в Планировщике заданий Windows (через COM Schedule.Service).
    /// Имя задания уникально для копии: OsEngineUpdate_&lt;hash&gt;.
    /// </summary>
    public static class AutoTaskService
    {
        public const string TaskPrefix = "OsEngineUpdate_";

        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonInteractiveToken = 3;

        private static dynamic RootFolder()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service")
                     ?? throw new InvalidOperationException("Планировщик заданий Windows недоступен.");
            dynamic service = Activator.CreateInstance(t);
            service.Connect();
            return service.GetFolder("\\");
        }

        public static AutoTaskInfo Query(string taskName)
        {
            dynamic task;

            try
            {
                task = RootFolder().GetTask(taskName);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                return new AutoTaskInfo { Installed = false };
            }

            string command = null;
            string arguments = null;

            try
            {
                dynamic actions = task.Definition.Actions;

                foreach (dynamic a in actions)
                {
                    command = a.Path;
                    arguments = a.Arguments;
                    break;
                }
            }
            catch
            {
                // нестандартное действие — не важно
            }

            int state = task.State;

            return new AutoTaskInfo
            {
                Installed = true,
                Enabled = task.Enabled,
                State = state switch { 1 => "отключено", 2 => "в очереди", 3 => "готово", 4 => "выполняется", _ => "неизвестно" },
                LastRun = ValidDate(task.LastRunTime),
                LastResult = task.LastTaskResult,
                NextRun = ValidDate(task.NextRunTime),
                Command = command,
                Arguments = arguments,
                Legacy = (command ?? "").Contains("powershell", StringComparison.OrdinalIgnoreCase)
                         || (arguments ?? "").Contains(".ps1", StringComparison.OrdinalIgnoreCase)
            };
        }

        public static void Install(CopyInfo copy, UpdaterConfig cfg, string exePath)
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string mode = cfg.IsDeltaMode ? "delta" : "mirror";
            string args = $"--update \"{copy.Root}\" --mode {mode} --silent";
            string start = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss");

            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>{Esc($"OsEngine auto update ({cfg.AutoMode}) for {copy.Root}")}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Esc(user)}</UserId>
    </LogonTrigger>
    <TimeTrigger>
      <StartBoundary>{start}</StartBoundary>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>PT{cfg.IntervalMinutes}M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{Esc(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{Esc(exePath)}</Command>
      <Arguments>{Esc(args)}</Arguments>
    </Exec>
  </Actions>
</Task>";

            try
            {
                RootFolder().RegisterTask(copy.TaskName, xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                throw AccessDenied(copy.TaskName);
            }
        }

        public static void Remove(string taskName)
        {
            try
            {
                RootFolder().DeleteTask(taskName, 0);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                // уже нет
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                throw AccessDenied(taskName);
            }
        }

        private static bool IsNotFound(Exception ex) => (uint)ex.HResult == 0x80070002 || ex is FileNotFoundException;

        private static bool IsAccessDenied(Exception ex) => (uint)ex.HResult == 0x80070005 || ex is UnauthorizedAccessException;

        private static Exception AccessDenied(string taskName)
        {
            return new UnauthorizedAccessException(
                $"Нет прав на изменение задания {taskName}: оно было создано от имени администратора. "
                + "Один раз запустите OsEngineUpdater.exe от имени администратора и повторите — дальше права не понадобятся.");
        }

        /// <summary>Копии OsEngine, для которых в Планировщике есть задание автообновления.</summary>
        public static List<string> DiscoverRoots()
        {
            List<string> roots = new List<string>();

            try
            {
                dynamic tasks = RootFolder().GetTasks(1);

                foreach (dynamic t in tasks)
                {
                    string name = t.Name;

                    if (!name.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (dynamic a in t.Definition.Actions)
                    {
                        string root = RootFromArguments((string)a.Arguments);

                        if (root != null)
                        {
                            roots.Add(root);
                        }

                        break;
                    }
                }
            }
            catch
            {
                // поиск — только подсказка
            }

            return roots;
        }

        /// <summary>Достаёт путь копии из аргументов задания (старый .ps1 или новый exe).</summary>
        public static string RootFromArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments))
            {
                return null;
            }

            string[] parts = arguments.Split('"');

            for (int i = 1; i < parts.Length; i += 2)
            {
                string p = parts[i];

                if (p.Length < 3)
                {
                    continue;
                }

                string dir = p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(p) : p;
                string root = CopyInfo.FindRoot(dir);

                if (root != null)
                {
                    return root;
                }
            }

            return null;
        }

        private static DateTime? ValidDate(DateTime d)
        {
            return d.Year < 2000 ? null : d;
        }

        private static string Esc(string s) => SecurityElement.Escape(s);
    }
}
