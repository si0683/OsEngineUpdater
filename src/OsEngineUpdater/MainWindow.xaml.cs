using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    public partial class MainWindow : Window
    {
        private const int LogTailLines = 400;
        private const int LogMaxChars = 400_000;

        private readonly ObservableCollection<CopyItem> _copies = new ObservableCollection<CopyItem>();
        private CopyItem _selected;
        private UpdaterConfig _savedConfig;
        private bool _loadingForm;
        private bool _dirty;
        private CancellationTokenSource _cts;
        private CopyItem _runningItem;

        public MainWindow()
        {
            InitializeComponent();
            CopiesList.ItemsSource = _copies;
            SetFormEnabled(false);
        }

        #region Copies

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            List<string> roots = await Task.Run(CopyRegistry.LoadWithDiscovery);

            foreach (string r in roots)
            {
                _copies.Add(new CopyItem(r));
            }

            if (_copies.Count > 0)
            {
                CopiesList.SelectedIndex = 0;
            }
            else
            {
                AppendLog(LogLevel.Info, "Копии OsEngine не найдены. Нажмите «Добавить копию…» и укажите папку, в которой лежит project.");
            }

            foreach (CopyItem item in _copies.ToList())
            {
                await RefreshAutoAsync(item);
            }

            foreach (CopyItem item in _copies.ToList())
            {
                await RefreshBehindAsync(item, false);
            }
        }

        private void CopiesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CopyItem item = CopiesList.SelectedItem as CopyItem;

            if (item == _selected)
            {
                return;
            }

            if (_dirty && _selected != null)
            {
                MessageBoxResult r = MessageBox.Show(this, $"Сохранить изменения настроек копии «{_selected.Name}»?",
                    "OsEngine Updater", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (r == MessageBoxResult.Cancel)
                {
                    CopiesList.SelectedItem = _selected;
                    return;
                }

                if (r == MessageBoxResult.Yes && !SaveSettings())
                {
                    CopiesList.SelectedItem = _selected;
                    return;
                }
            }

            _selected = item;
            ShowSelected();
        }

        private void ShowSelected()
        {
            if (_selected == null)
            {
                TxtCopyName.Text = "—";
                TxtCopyRoot.Text = "";
                TxtVersion.Text = TxtBehind.Text = TxtAuto.Text = "—";
                TxtVersionDate.Text = TxtBehindHint.Text = TxtAutoDetails.Text = "";
                TxtLog.Clear();
                SetFormEnabled(false);
                UpdateButtons();
                return;
            }

            TxtCopyName.Text = _selected.Name;
            TxtCopyRoot.Text = _selected.Root;

            try
            {
                _savedConfig = UpdaterConfig.Load(_selected.Copy);
                FillForm(_savedConfig);
                SetFormEnabled(true);
            }
            catch (Exception ex)
            {
                SetFormEnabled(false);
                AppendLog(LogLevel.Error, "Не удалось прочитать настройки: " + ex.Message);
            }

            LoadLogTail();
            UpdateStatusCards();
            UpdateButtons();
            _ = RefreshBehindAsync(_selected, false);
        }

        private async void BtnAddCopy_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dlg = new OpenFolderDialog { Title = "Папка копии OsEngine (где лежит project)" };

            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            string root = CopyInfo.FindRoot(dlg.FolderName);

            if (root == null)
            {
                MessageBox.Show(this, "В этой папке (и выше) не найден project\\OsEngine\\OsEngine.csproj.\n\nУкажите корень копии OsEngine.",
                    "OsEngine Updater", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CopyItem item = AddCopy(root);
            CopiesList.SelectedItem = item;
            await RefreshAutoAsync(item);
        }

        private async void BtnNewCopy_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dlg = new OpenFolderDialog { Title = "Пустая папка для новой копии OsEngine" };

            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            string target = CopyInfo.NormalizeRoot(dlg.FolderName);
            bool notEmpty = Directory.EnumerateFileSystemEntries(target).Any();

            string question = $"Скачать свежую копию OsEngine (~200 МБ) в\n{target}?"
                              + (notEmpty ? "\n\nПапка не пуста: официальные файлы будут перезаписаны, остальные сохранятся." : "");

            if (MessageBox.Show(this, question, "Новая копия", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            UpdaterConfig cfg = _savedConfig?.Clone() ?? new UpdaterConfig();
            cfg.UserProtectedPaths = new List<string>();
            cfg.ProtectedPaths = null;

            TxtLog.Clear();
            TxtLogTitle.Text = "Лог — новая копия";
            int code = await RunOperationAsync(null, (ct, sink) => UpdateEngine.BootstrapAsync(target, cfg, sink, ct));

            if (code == ExitCodes.Ok && CopyInfo.IsCopyRoot(target))
            {
                CopyItem item = AddCopy(target);
                CopiesList.SelectedItem = item;
                await RefreshAutoAsync(item);
            }
        }

        private void BtnRemoveCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            string msg = $"Убрать копию «{_selected.Name}» из списка?\n\nФайлы не удаляются.";

            if (_selected.Auto?.Installed == true)
            {
                msg += " Автообновление этой копии будет выключено.";
            }

            if (MessageBox.Show(this, msg, "OsEngine Updater", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            try
            {
                if (_selected.Auto?.Installed == true)
                {
                    AutoTaskService.Remove(_selected.Copy.TaskName);
                }
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Не удалось удалить задание: " + ex.Message);
            }

            _dirty = false;
            _copies.Remove(_selected);
            SaveRegistry();
            CopiesList.SelectedIndex = _copies.Count > 0 ? 0 : -1;
        }

        private CopyItem AddCopy(string root)
        {
            CopyItem existing = _copies.FirstOrDefault(c => string.Equals(c.Root, CopyInfo.NormalizeRoot(root), StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return existing;
            }

            CopyItem item = new CopyItem(root);
            _copies.Add(item);
            SaveRegistry();
            return item;
        }

        private void SaveRegistry()
        {
            try
            {
                CopyRegistry.Save(_copies.Select(c => c.Root));
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Не удалось сохранить список копий: " + ex.Message);
            }
        }

        #endregion

        #region Status

        private async Task RefreshAutoAsync(CopyItem item)
        {
            try
            {
                item.Auto = await Task.Run(() => AutoTaskService.Query(item.Copy.TaskName));
            }
            catch (Exception ex)
            {
                item.Auto = null;
                item.AutoError = ex.Message;
            }

            item.UpdateSummary();

            if (item == _selected)
            {
                UpdateStatusCards();
                UpdateButtons();
            }
        }

        private async Task RefreshBehindAsync(CopyItem item, bool force)
        {
            if (!force && item.BehindCheckedAt != null && DateTime.Now - item.BehindCheckedAt < TimeSpan.FromMinutes(5))
            {
                return;
            }

            try
            {
                UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
                using UpdateEngine engine = new UpdateEngine(item.Copy, cfg, new UpdateLog(null, null));
                (int? behind, _) = await engine.GetBehindAsync(CancellationToken.None);
                item.Behind = behind;
                item.BehindError = null;
            }
            catch (Exception ex)
            {
                item.BehindError = ex.Message;
            }

            item.BehindCheckedAt = DateTime.Now;
            item.UpdateSummary();

            if (item == _selected)
            {
                UpdateStatusCards();
            }
        }

        private void UpdateStatusCards()
        {
            if (_selected == null)
            {
                return;
            }

            UpdaterState state = _selected.Copy.LoadState();

            if (state?.LastSha == null)
            {
                TxtVersion.Text = "не обновлялась утилитой";
                TxtVersionDate.Text = "Нажмите «Обновить сейчас»";
            }
            else
            {
                TxtVersion.Text = "коммит " + UpdateEngine.Short(state.LastSha);
                TxtVersionDate.Text = state.UpdatedLocal is DateTime dt ? $"обновлено {dt:dd.MM.yyyy HH:mm}" : "";
            }

            // GitHub
            if (_selected.BehindError != null)
            {
                SetText(TxtBehind, "нет данных", FindBrush("FgDim"));
                TxtBehindHint.Text = _selected.BehindError;
            }
            else if (_selected.BehindCheckedAt == null)
            {
                SetText(TxtBehind, "проверяю…", FindBrush("FgDim"));
                TxtBehindHint.Text = "";
            }
            else
            {
                if (_selected.Behind == null)
                {
                    SetText(TxtBehind, "неизвестно", FindBrush("FgDim"));
                }
                else if (_selected.Behind == 0)
                {
                    SetText(TxtBehind, "актуально", FindBrush("Good"));
                }
                else
                {
                    int n = _selected.Behind.Value;
                    SetText(TxtBehind, $"отстаёт на {n} {UpdateEngine.Plural(n, "коммит", "коммита", "коммитов")}", FindBrush("Warn"));
                }

                TxtBehindHint.Text = $"проверено в {_selected.BehindCheckedAt:HH:mm}";
            }

            // авто
            AutoTaskInfo a = _selected.Auto;
            int interval = _savedConfig?.IntervalMinutes ?? 1440;

            if (a == null)
            {
                SetText(TxtAuto, "нет данных", FindBrush("FgDim"));
                TxtAutoDetails.Text = _selected.AutoError ?? "";
            }
            else if (!a.Installed)
            {
                SetText(TxtAuto, "выключено", FindBrush("FgDim"));
                TxtAutoDetails.Text = "Обновление только вручную";
            }
            else if (a.Legacy)
            {
                SetText(TxtAuto, "старое задание (.ps1)", FindBrush("Warn"));
                TxtAutoDetails.Text = "Нажмите «Перевести авто на exe»";
            }
            else
            {
                bool exeMissing = a.Command != null && !File.Exists(a.Command);
                SetText(TxtAuto, exeMissing ? "exe не найден" : (a.Enabled ? $"каждые {FormatInterval(interval)}" : "отключено"),
                    exeMissing ? FindBrush("Bad") : FindBrush(a.Enabled ? "Good" : "Warn"));

                StringBuilder sb = new StringBuilder();
                sb.Append(a.LastRun is DateTime lr ? $"последний: {lr:dd.MM HH:mm} — {a.LastResultText}" : "ещё не запускалось");

                if (a.NextRun is DateTime nr)
                {
                    sb.Append($"\nследующий: {nr:dd.MM HH:mm}");
                }

                if (exeMissing)
                {
                    sb.Append("\nВключите авто заново из текущего exe");
                }

                TxtAutoDetails.Text = sb.ToString();
            }
        }

        private static string FormatInterval(int minutes)
        {
            if (minutes % 1440 == 0) return minutes == 1440 ? "сутки" : $"{minutes / 1440} сут.";
            if (minutes % 60 == 0) return $"{minutes / 60} ч";
            return $"{minutes} мин";
        }

        #endregion

        #region Settings form

        private void FillForm(UpdaterConfig cfg)
        {
            _loadingForm = true;

            SelectByTag(CmbProfile, cfg.BinProtected ? UpdaterConfig.ProfileFromSource : UpdaterConfig.ProfilePrebuilt);
            SelectByTag(CmbBuild, cfg.BuildAfterUpdate == null ? "auto" : (cfg.BuildAfterUpdate.Value ? "yes" : "no"));
            TxtBuildCommand.Text = cfg.BuildCommand ?? "";
            SelectByTag(CmbMode, cfg.IsDeltaMode ? UpdaterConfig.ModeDelta : UpdaterConfig.ModeMirror);
            TxtInterval.Text = cfg.IntervalMinutes.ToString();
            TxtProtected.Text = string.Join(Environment.NewLine, cfg.UserProtectedPaths);
            TxtOwner.Text = cfg.Owner;
            TxtRepo.Text = cfg.Repo;
            TxtBranch.Text = cfg.Branch;
            PwdToken.Password = cfg.GitHubToken ?? "";

            _loadingForm = false;
            SetDirty(false);
            UpdateProfileHint();
        }

        private UpdaterConfig ReadForm()
        {
            if (!int.TryParse(TxtInterval.Text.Trim(), out int interval) || interval < 1)
            {
                throw new FormatException("Интервал авто — целое число минут, не меньше 1.");
            }

            UpdaterConfig cfg = _savedConfig?.Clone() ?? new UpdaterConfig();
            cfg.Profile = TagOf(CmbProfile) ?? UpdaterConfig.ProfilePrebuilt;
            cfg.BuildAfterUpdate = TagOf(CmbBuild) switch { "yes" => true, "no" => false, _ => null };
            cfg.BuildCommand = string.IsNullOrWhiteSpace(TxtBuildCommand.Text) ? null : TxtBuildCommand.Text.Trim();
            cfg.AutoMode = TagOf(CmbMode) ?? UpdaterConfig.ModeMirror;
            cfg.AutoIntervalMinutes = interval;
            cfg.UserProtectedPaths = TxtProtected.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            cfg.Owner = string.IsNullOrWhiteSpace(TxtOwner.Text) ? "AlexWan" : TxtOwner.Text.Trim();
            cfg.Repo = string.IsNullOrWhiteSpace(TxtRepo.Text) ? "OsEngine" : TxtRepo.Text.Trim();
            cfg.Branch = string.IsNullOrWhiteSpace(TxtBranch.Text) ? "master" : TxtBranch.Text.Trim();
            cfg.GitHubToken = PwdToken.Password.Trim();
            return cfg;
        }

        private bool SaveSettings()
        {
            if (_selected == null)
            {
                return false;
            }

            UpdaterConfig cfg;

            try
            {
                cfg = ReadForm();
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                cfg.Save(_selected.Copy);
                _savedConfig = cfg;
                SetDirty(false);
                AppendLog(LogLevel.Ok, "Настройки сохранены: " + _selected.Copy.ConfigPath);

                // задание в Планировщике берёт интервал и режим из настроек — перерегистрируем
                if (_selected.Auto?.Installed == true && !_selected.Auto.Legacy)
                {
                    AutoTaskService.Install(_selected.Copy, cfg, Environment.ProcessPath);
                    AppendLog(LogLevel.Ok, $"Задание автообновления обновлено: {cfg.AutoMode}, каждые {cfg.IntervalMinutes} мин.");
                    _ = RefreshAutoAsync(_selected);
                }

                UpdateStatusCards();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void AnySetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingForm || _selected == null)
            {
                return;
            }

            SetDirty(true);

            if (sender == CmbProfile || sender == CmbBuild)
            {
                UpdateProfileHint();
            }
        }

        private void IntervalPreset_Click(object sender, RoutedEventArgs e)
        {
            TxtInterval.Text = (string)((Button)sender).Tag;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveSettings();

        private void BtnRevert_Click(object sender, RoutedEventArgs e)
        {
            if (_savedConfig != null)
            {
                FillForm(_savedConfig);
            }
        }

        private void UpdateProfileHint()
        {
            bool fromSource = TagOf(CmbProfile) == UpdaterConfig.ProfileFromSource;
            string build = TagOf(CmbBuild) switch
            {
                "yes" => "сборка после обновления — всегда",
                "no" => "сборки нет",
                _ => fromSource ? "после обновления запускается сборка" : "сборки нет"
            };

            TxtProfileHint.Text = fromSource
                ? "Папка bin защищена — ваша сборка не затирается; " + build + "."
                : "Папка bin обновляется (bin/Debug/OsEngine.exe остаётся актуальным); " + build
                  + ". Свой код держите в bin/Debug/Custom.";
        }

        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            TxtDirty.Text = dirty ? "есть несохранённые изменения" : "";
            BtnSave.IsEnabled = dirty;
            BtnRevert.IsEnabled = dirty;
        }

        private void SetFormEnabled(bool enabled)
        {
            SettingsGrid.IsEnabled = enabled;

            if (!enabled)
            {
                BtnSave.IsEnabled = false;
                BtnRevert.IsEnabled = false;
            }
        }

        private static void SelectByTag(ComboBox cmb, string tag)
        {
            foreach (ComboBoxItem it in cmb.Items)
            {
                if (string.Equals((string)it.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = it;
                    return;
                }
            }

            cmb.SelectedIndex = 0;
        }

        private static string TagOf(ComboBox cmb) => (cmb.SelectedItem as ComboBoxItem)?.Tag as string;

        #endregion

        #region Actions

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync(UpdateMode.Mirror);

        private async void BtnCheck_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync(UpdateMode.Check);

        private async void BtnFull_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Скачать архив репозитория (~200 МБ) и перезаписать все официальные файлы этой копии?\n\nВаши собственные файлы не трогаются.",
                    "Полная перекачка", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                await RunUpdateAsync(UpdateMode.Full);
            }
        }

        private async Task RunUpdateAsync(UpdateMode mode)
        {
            if (_selected == null || !ConfirmUnsaved())
            {
                return;
            }

            CopyItem item = _selected;
            UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
            bool force = ChkForce.IsChecked == true;

            AppendSeparator();
            await RunOperationAsync(item, async (ct, sink) =>
            {
                UpdateLog log = new UpdateLog(item.Copy.LogPath, sink);
                using UpdateEngine engine = new UpdateEngine(item.Copy, cfg, log) { Force = force };
                return await engine.RunAsync(mode, ct);
            });

            await RefreshBehindAsync(item, true);
            UpdateStatusCards();
        }

        private async void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            CopyItem item = _selected;

            try
            {
                if (item.Auto?.Installed == true && !item.Auto.Legacy)
                {
                    AutoTaskService.Remove(item.Copy.TaskName);
                    AppendLog(LogLevel.Ok, $"Автообновление выключено ({item.Copy.TaskName} удалено из Планировщика).");
                }
                else
                {
                    if (!ConfirmUnsaved())
                    {
                        return;
                    }

                    UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
                    AutoTaskService.Install(item.Copy, cfg, Environment.ProcessPath);
                    AppendLog(LogLevel.Ok, $"Автообновление включено: {cfg.AutoMode}, каждые {cfg.IntervalMinutes} мин и при входе в систему ({item.Copy.TaskName}).");
                    AppendLog(LogLevel.Info, "Задание запускает " + Environment.ProcessPath + " — не перемещайте этот exe, иначе включите авто заново.");
                }
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Планировщик заданий: " + ex.Message);
                MessageBox.Show(this, "Не удалось изменить задание в Планировщике:\n" + ex.Message
                                      + "\n\nЕсли не хватает прав — запустите OsEngineUpdater от имени администратора.",
                    "Автообновление", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await RefreshAutoAsync(item);
        }

        private void BtnVs_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            AppendSeparator();

            try
            {
                VsRefresher.Run(_selected.Copy, new UpdateLog(_selected.Copy.LogPath, (l, m) => AppendLog(l, m)));
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Ошибка: " + ex.Message);
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null)
            {
                OpenPath(_selected.Root);
            }
        }

        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null && File.Exists(_selected.Copy.LogPath))
            {
                OpenPath(_selected.Copy.LogPath);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnCancel.IsEnabled = false;
        }

        private bool ConfirmUnsaved()
        {
            if (!_dirty)
            {
                return true;
            }

            MessageBoxResult r = MessageBox.Show(this, "Есть несохранённые настройки. Сохранить перед запуском?",
                "OsEngine Updater", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (r == MessageBoxResult.Cancel)
            {
                return false;
            }

            if (r == MessageBoxResult.Yes)
            {
                return SaveSettings();
            }

            return true;
        }

        /// <summary>Запуск долгой операции: блокирует кнопки, пишет лог в окно, даёт отмену.</summary>
        private async Task<int> RunOperationAsync(CopyItem item, Func<CancellationToken, Action<LogLevel, string>, Task<int>> body)
        {
            _cts = new CancellationTokenSource();
            _runningItem = item;
            UpdateButtons();

            void Sink(LogLevel level, string msg)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (item == null || item == _selected)
                    {
                        AppendLog(level, msg);
                    }
                });
            }

            int code;

            try
            {
                CancellationToken ct = _cts.Token;
                code = await Task.Run(() => body(ct, Sink));
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Ошибка: " + ex.Message);
                code = ExitCodes.Error;
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _runningItem = null;
                UpdateButtons();
            }

            return code;
        }

        private bool IsBusy => _cts != null;

        private void UpdateButtons()
        {
            bool has = _selected != null;
            bool busy = IsBusy;

            BtnUpdate.IsEnabled = has && !busy;
            BtnCheck.IsEnabled = has && !busy;
            BtnFull.IsEnabled = has && !busy;
            BtnAuto.IsEnabled = has;
            BtnVs.IsEnabled = has && !busy;
            BtnOpenFolder.IsEnabled = has;
            BtnCancel.IsEnabled = busy;
            BtnAddCopy.IsEnabled = !busy;
            BtnNewCopy.IsEnabled = !busy;
            BtnRemoveCopy.IsEnabled = has && !busy;
            CopiesList.IsEnabled = !busy;

            AutoTaskInfo a = _selected?.Auto;
            BtnAuto.Content = a?.Installed == true
                ? (a.Legacy ? "Перевести авто на exe" : "Выключить авто")
                : "Включить авто";
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (IsBusy)
            {
                if (MessageBox.Show(this, "Операция ещё выполняется. Прервать и закрыть?", "OsEngine Updater",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                _cts?.Cancel();
            }
            else if (_dirty)
            {
                MessageBoxResult r = MessageBox.Show(this, "Сохранить изменения настроек?", "OsEngine Updater",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !SaveSettings()))
                {
                    e.Cancel = true;
                }
            }
        }

        #endregion

        #region Log view

        private void LoadLogTail()
        {
            TxtLog.Clear();
            TxtLogTitle.Text = "Лог — " + _selected.Name;
            string path = _selected.Copy.LogPath;

            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader sr = new StreamReader(fs, Encoding.UTF8);
                Queue<string> tail = new Queue<string>();
                string line;

                while ((line = sr.ReadLine()) != null)
                {
                    tail.Enqueue(line);

                    if (tail.Count > LogTailLines)
                    {
                        tail.Dequeue();
                    }
                }

                TxtLog.Text = string.Join(Environment.NewLine, tail) + Environment.NewLine;
                TxtLog.ScrollToEnd();
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Warn, "Не удалось прочитать лог: " + ex.Message);
            }
        }

        private void AppendSeparator()
        {
            if (TxtLog.Text.Length > 0)
            {
                TxtLog.AppendText(Environment.NewLine);
            }
        }

        private void AppendLog(LogLevel level, string message)
        {
            string prefix = level switch
            {
                LogLevel.Warn => "! ",
                LogLevel.Error => "✖ ",
                LogLevel.Ok => "✔ ",
                _ => ""
            };

            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}{Environment.NewLine}");

            if (TxtLog.Text.Length > LogMaxChars)
            {
                TxtLog.Text = TxtLog.Text.Substring(TxtLog.Text.Length - LogMaxChars / 2);
            }

            TxtLog.ScrollToEnd();
        }

        #endregion

        #region Helpers

        private Brush FindBrush(string key) => (Brush)FindResource(key);

        private static void SetText(TextBlock tb, string text, Brush brush)
        {
            tb.Text = text;
            tb.Foreground = brush;
        }

        private static void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // не открылось — не критично
            }
        }

        #endregion
    }

    /// <summary>Строка списка копий.</summary>
    public sealed class CopyItem : INotifyPropertyChanged
    {
        public CopyItem(string root)
        {
            Copy = new CopyInfo(root);
            UpdateSummary();
        }

        public CopyInfo Copy { get; }
        public string Root => Copy.Root;
        public string Name => Copy.Name;

        public AutoTaskInfo Auto { get; set; }
        public string AutoError { get; set; }
        public int? Behind { get; set; }
        public string BehindError { get; set; }
        public DateTime? BehindCheckedAt { get; set; }

        private string _summary;
        public string Summary
        {
            get => _summary;
            private set { _summary = value; OnChanged(); }
        }

        private Brush _summaryBrush;
        public Brush SummaryBrush
        {
            get => _summaryBrush;
            private set { _summaryBrush = value; OnChanged(); }
        }

        public void UpdateSummary()
        {
            string auto = Auto == null ? "авто: ?" : (!Auto.Installed ? "авто выкл" : (Auto.Legacy ? "авто: старый .ps1" : "авто вкл"));
            string gh = BehindCheckedAt == null || BehindError != null || Behind == null
                ? ""
                : (Behind == 0 ? " · актуально" : $" · отстаёт на {Behind}");

            Summary = auto + gh;

            string key = Behind > 0 || Auto?.Legacy == true ? "Warn" : (Auto?.Installed == true ? "Good" : "FgDim");
            SummaryBrush = (Brush)Application.Current.FindResource(key);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
