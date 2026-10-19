using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Слежение, пока окно открыто: периодическая проверка GitHub по всем копиям
    /// и отслеживание файлов копий (обновление из Планировщика сразу видно в окне).
    /// </summary>
    public partial class MainWindow
    {
        private const string BaseTitle = "OsEngine Updater";

        private readonly DispatcherTimer _watchTimer = new DispatcherTimer();
        private readonly Dictionary<CopyItem, FileSystemWatcher> _watchers = new Dictionary<CopyItem, FileSystemWatcher>();
        private readonly Dictionary<CopyItem, DispatcherTimer> _debounce = new Dictionary<CopyItem, DispatcherTimer>();
        private readonly Dictionary<CopyItem, DateTime> _ownWrite = new Dictionary<CopyItem, DateTime>();
        private DateTime? _nextCheck;
        private bool _checking;
        private bool _watchReady;

        private void InitWatch()
        {
            SelectWatchItem(_userSettings.EffectiveAutoMinutes);

            foreach (CopyItem item in _copies)
            {
                AttachWatcher(item);
            }

            _watchTimer.Tick += async (_, _) => await CheckAllAsync(true);
            _watchReady = true;
            RestartWatchTimer();
            UpdateWatchTitle();
        }

        private void SelectWatchItem(int minutes)
        {
            List<ComboBoxItem> items = CmbWatch.Items.Cast<ComboBoxItem>().ToList();

            // ближайшее из списка (старые настройки могли хранить другое значение)
            CmbWatch.SelectedItem = items.OrderBy(i => Math.Abs(int.Parse((string)i.Tag) - minutes)).First();
        }

        private async void CmbWatch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_watchReady || CmbWatch.SelectedItem is not ComboBoxItem it)
            {
                return;
            }

            int minutes = int.Parse((string)it.Tag);

            if (minutes == _userSettings.AutoMinutes)
            {
                return;
            }

            try
            {
                // перечитываем, чтобы не затереть изменения из «Мои аккаунты и репозитории»
                UserSettings s = UserSettings.Load();
                s.AutoMinutes = minutes;
                s.Save();
                _userSettings = s;
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Warn, "Не удалось сохранить частоту автообновления: " + ex.Message);
            }

            RestartWatchTimer();
            AppendLog(LogLevel.Ok, $"Автообновление: {UserSettings.EveryText(minutes)}.");
            await ResyncTasksAsync(true);
        }

        /// <summary>
        /// Задания Планировщика всех копий с включённым авто — на общую частоту и с --quick.
        /// verbose = false: тихо, только если задание отличается (при запуске окна).
        /// </summary>
        private async Task ResyncTasksAsync(bool verbose)
        {
            int minutes = _userSettings.EffectiveAutoMinutes;

            foreach (CopyItem item in _copies.ToList())
            {
                AutoTaskInfo a = item.Auto;

                if (a == null || !a.Installed || a.Legacy || (!verbose && !a.NeedsResync(minutes)))
                {
                    continue;
                }

                try
                {
                    UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
                    await Task.Run(() => AutoTaskService.Install(item.Copy, cfg, Environment.ProcessPath, minutes));

                    if (verbose)
                    {
                        AppendLog(LogLevel.Info, $"Задание автообновления копии {item.Name} перенастроено: {UserSettings.EveryText(minutes)}.");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog(LogLevel.Warn, $"Задание автообновления копии {item.Name} осталось со старым расписанием: {ex.Message}");
                }

                await RefreshAutoAsync(item);
            }
        }

        private void RestartWatchTimer()
        {
            _watchTimer.Stop();
            int minutes = _userSettings.EffectiveAutoMinutes;
            _watchTimer.Interval = TimeSpan.FromMinutes(minutes);
            _watchTimer.Start();
            _nextCheck = DateTime.Now.AddMinutes(minutes);
            UpdateWatchText();
        }

        private void UpdateWatchText()
        {
            string last = _copies.Where(c => c.BehindCheckedAt != null).Select(c => c.BehindCheckedAt.Value).DefaultIfEmpty().Max() is DateTime d && d != default
                ? $"проверено в {d:HH:mm}"
                : "ещё не проверялось";
            TxtWatch.Text = _nextCheck is DateTime n ? $"{last} · следующая в {n:HH:mm}" : last;
        }

        /// <summary>
        /// Проверка всех копий: один запрос «последний коммит» на источник (Owner/Repo/Branch),
        /// сравнение — только если копия отстаёт (результат кэшируется).
        /// Копии с включённым авто, которые отстают, сразу обновляются; об остальных — уведомление.
        /// </summary>
        private async Task CheckAllAsync(bool notify)
        {
            if (_checking)
            {
                return;
            }

            _checking = true;
            _nextCheck = DateTime.Now.AddMinutes(_userSettings.EffectiveAutoMinutes);
            List<CopyItem> toUpdate = new List<CopyItem>();

            try
            {
                Dictionary<string, string> heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (CopyItem item in _copies.ToList())
                {
                    if (item == _runningItem)
                    {
                        continue;
                    }

                    int? before = item.Behind;
                    string head;

                    try
                    {
                        UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
                        string source = $"{cfg.Owner}/{cfg.Repo}@{cfg.Branch}";

                        if (!heads.TryGetValue(source, out head))
                        {
                            using UpdateEngine engine = new UpdateEngine(item.Copy, cfg, new UpdateLog(null, null));
                            head = await engine.GetHeadShaAsync(CancellationToken.None);
                            heads[source] = head;
                        }
                    }
                    catch (Exception ex)
                    {
                        item.BehindError = ex.Message;
                        item.BehindCheckedAt = DateTime.Now;
                        item.UpdateSummary();
                        continue;
                    }

                    await RefreshBehindAsync(item, true, head);
                    await RefreshAutoAsync(item);

                    bool autoOn = item.Auto?.Installed == true && !item.Auto.Legacy && item.Auto.Enabled;

                    if (item.Behind > 0 && autoOn)
                    {
                        toUpdate.Add(item);
                    }
                    else if (notify && item.Behind > 0 && (before == null || before == 0 || item.Behind > before))
                    {
                        int n = item.Behind.Value;
                        AppendLog(LogLevel.Warn, $"Вышло обновление OsEngine: копия {item.Name} отстаёт на {n} {UpdateEngine.Plural(n, "коммит", "коммита", "коммитов")} (автообновление выключено).");
                        Flash();
                    }
                }
            }
            finally
            {
                _checking = false;
                UpdateWatchText();
                UpdateWatchTitle();
            }

            // копии с включённым авто обновляем сразу, по одной (если окно не занято другой операцией)
            foreach (CopyItem item in toUpdate)
            {
                if (IsBusy || !_copies.Contains(item))
                {
                    break;
                }

                int n = item.Behind ?? 0;
                AppendLog(LogLevel.Info, $"Автообновление: копия {item.Name} отстаёт на {n} {UpdateEngine.Plural(n, "коммит", "коммита", "коммитов")} — обновляю.");
                UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
                await RunCopyUpdateAsync(item, cfg.IsDeltaMode ? UpdateMode.Delta : UpdateMode.Mirror, false, true);
            }
        }

        /// <summary>Число отстающих копий в заголовке окна (видно и на панели задач).</summary>
        private void UpdateWatchTitle()
        {
            int behind = _copies.Count(c => c.Behind > 0);
            Title = behind > 0 ? $"{BaseTitle} — есть обновления ({behind})" : BaseTitle;
        }

        /// <summary>Отметка: лог копии только что писало само окно.</summary>
        private void MarkOwnWrite(CopyItem item)
        {
            if (item != null)
            {
                _ownWrite[item] = DateTime.Now;
            }
        }

        #region Files of copies

        /// <summary>Следим за state.json и update.log копии: их меняет автообновление из Планировщика.</summary>
        private void AttachWatcher(CopyItem item)
        {
            if (_watchers.ContainsKey(item))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(item.Copy.DataDir);
                FileSystemWatcher w = new FileSystemWatcher(item.Copy.DataDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                w.Changed += (_, e) => OnCopyFileChanged(item, e.Name);
                w.Created += (_, e) => OnCopyFileChanged(item, e.Name);
                w.Renamed += (_, e) => OnCopyFileChanged(item, e.Name);
                w.EnableRaisingEvents = true;
                _watchers[item] = w;
            }
            catch
            {
                // слежение — удобство; без него всё работает по таймеру
            }
        }

        private void DetachWatcher(CopyItem item)
        {
            if (_watchers.Remove(item, out FileSystemWatcher w))
            {
                w.Dispose();
            }

            if (_debounce.Remove(item, out DispatcherTimer t))
            {
                t.Stop();
            }
        }

        private void OnCopyFileChanged(CopyItem item, string name)
        {
            if (name == null || !(name.Equals("state.json", StringComparison.OrdinalIgnoreCase)
                                  || name.Equals("update.log", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // событий приходит много подряд — реагируем один раз, когда запись закончится
            Dispatcher.BeginInvoke(() =>
            {
                if (!_debounce.TryGetValue(item, out DispatcherTimer t))
                {
                    t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    t.Tick += async (_, _) =>
                    {
                        t.Stop();
                        await OnCopyChangedAsync(item);
                    };
                    _debounce[item] = t;
                }

                t.Stop();
                t.Start();
            });
        }

        private async Task OnCopyChangedAsync(CopyItem item)
        {
            if (!_copies.Contains(item) || item == _runningItem)
            {
                // операцию из этого окна и так видно в логе
                return;
            }

            // копия обновилась извне (Планировщик или другой экземпляр): версия, отставание, лог
            await RefreshBehindAsync(item, true, item.LastHead);
            await RefreshAutoAsync(item);

            if (item == _selected)
            {
                UpdateStatusCards();

                // лог своей операции уже на экране — не перечитываем, чтобы не пропали строки только окна
                bool own = _ownWrite.TryGetValue(item, out DateTime at) && DateTime.Now - at < TimeSpan.FromSeconds(5);

                if (!own)
                {
                    LoadLogTail();
                }
            }

            UpdateWatchText();
            UpdateWatchTitle();
        }

        #endregion

        #region Taskbar flash

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Hwnd;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        /// <summary>Мигнуть кнопкой на панели задач, если окно не активно.</summary>
        private void Flash()
        {
            if (IsActive)
            {
                return;
            }

            const uint flashAll = 3;
            const uint untilForeground = 12;
            FlashInfo fi = new FlashInfo
            {
                Hwnd = new WindowInteropHelper(this).Handle,
                Flags = flashAll | untilForeground,
                Count = uint.MaxValue
            };
            fi.Size = (uint)Marshal.SizeOf(fi);
            FlashWindowEx(ref fi);
        }

        #endregion
    }
}
