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
        private UserSettings _userSettings = UserSettings.Load();
        private readonly ObservableCollection<ExclusionItem> _protected = new ObservableCollection<ExclusionItem>();

        public MainWindow()
        {
            InitializeComponent();
            CopiesList.ItemsSource = _copies;
            LstProtected.ItemsSource = _protected;
            RestorePaneWidths();
            _protected.CollectionChanged += (_, _) => UpdateProtectedEmpty();
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

            InitWatch();
            InitSelfUpdate();
            await ResyncTasksAsync(false);
            await CheckAllAsync(false);
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
                TxtLogTitle.Text = "Лог";
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

        private async void BtnDeleteCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            CopyItem item = _selected;
            DeleteCopyWindow dlg = new DeleteCopyWindow(item.Copy, _copies.Where(c => c != item).Select(c => c.Copy)) { Owner = this };

            if (dlg.ShowDialog() == true)
            {
                _dirty = false;
                DetachWatcher(item);
                _copies.Remove(item);
                SaveRegistry();
                CopiesList.SelectedIndex = _copies.Count > 0 ? 0 : -1;
                AppendLog(LogLevel.Ok, $"Копия {item.Root} удалена (папка в Корзине), автообновление выключено.");
                return;
            }

            // при отмене в окне Windows задание уже могло быть снято
            await RefreshAutoAsync(item);
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
            AttachWatcher(item);
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

        /// <param name="knownHead">Последний коммит, если уже известен (общая проверка нескольких копий).</param>
        private async Task RefreshBehindAsync(CopyItem item, bool force, string knownHead = null)
        {
            if (!force && item.BehindCheckedAt != null && DateTime.Now - item.BehindCheckedAt < TimeSpan.FromMinutes(5))
            {
                return;
            }

            try
            {
                UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);
                using UpdateEngine engine = new UpdateEngine(item.Copy, cfg, new UpdateLog(null, null));
                (int? behind, string head) = await engine.GetBehindAsync(CancellationToken.None, knownHead);
                item.Behind = behind;
                item.LastHead = head;
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

            UpdateWatchText();
            UpdateWatchTitle();
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
            int interval = _userSettings.EffectiveAutoMinutes;

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
                SetText(TxtAuto, exeMissing ? "exe не найден" : (a.Enabled ? $"{UserSettings.EveryText(interval)}" : "отключено"),
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

        #endregion

        #region Settings form

        private void FillForm(UpdaterConfig cfg)
        {
            _loadingForm = true;

            SelectByTag(CmbProfile, cfg.BinProtected ? UpdaterConfig.ProfileFromSource : UpdaterConfig.ProfilePrebuilt);
            SelectByTag(CmbBuild, cfg.BuildAfterUpdate == null ? "auto" : (cfg.BuildAfterUpdate.Value ? "yes" : "no"));
            TxtBuildCommand.Text = cfg.BuildCommand ?? "";
            SelectByTag(CmbMode, cfg.IsDeltaMode ? UpdaterConfig.ModeDelta : UpdaterConfig.ModeMirror);
            _protected.Clear();

            foreach (string pattern in cfg.UserProtectedPaths)
            {
                _protected.Add(new ExclusionItem(pattern));
            }
            TxtOwner.Text = cfg.Owner;
            TxtRepo.Text = cfg.Repo;
            TxtBranch.Text = cfg.Branch;
            PwdToken.Password = cfg.GitHubToken ?? "";
            TxtRobotsDir.Text = cfg.EffectiveRobotsDir;
            TxtIndicatorsDir.Text = cfg.EffectiveIndicatorsDir;

            _loadingForm = false;
            SetDirty(false);
            UpdateProfileHint();
        }

        private UpdaterConfig ReadForm()
        {
            UpdaterConfig cfg = _savedConfig?.Clone() ?? new UpdaterConfig();
            cfg.Profile = TagOf(CmbProfile) ?? UpdaterConfig.ProfilePrebuilt;
            cfg.BuildAfterUpdate = TagOf(CmbBuild) switch { "yes" => true, "no" => false, _ => null };
            cfg.BuildCommand = string.IsNullOrWhiteSpace(TxtBuildCommand.Text) ? null : TxtBuildCommand.Text.Trim();
            cfg.AutoMode = TagOf(CmbMode) ?? UpdaterConfig.ModeMirror;
            cfg.UserProtectedPaths = _protected.Select(x => x.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            cfg.Owner = string.IsNullOrWhiteSpace(TxtOwner.Text) ? "AlexWan" : TxtOwner.Text.Trim();
            cfg.Repo = string.IsNullOrWhiteSpace(TxtRepo.Text) ? "OsEngine" : TxtRepo.Text.Trim();
            cfg.Branch = string.IsNullOrWhiteSpace(TxtBranch.Text) ? "master" : TxtBranch.Text.Trim();
            cfg.GitHubToken = PwdToken.Password.Trim();
            cfg.RobotsDir = string.IsNullOrWhiteSpace(TxtRobotsDir.Text) ? UpdaterConfig.DefaultRobotsDir : TxtRobotsDir.Text.Trim();
            cfg.IndicatorsDir = string.IsNullOrWhiteSpace(TxtIndicatorsDir.Text) ? UpdaterConfig.DefaultIndicatorsDir : TxtIndicatorsDir.Text.Trim();
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
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // задание в Планировщике берёт интервал и режим из настроек — перерегистрируем
            if (_selected.Auto?.Installed == true && !_selected.Auto.Legacy)
            {
                try
                {
                    AutoTaskService.Install(_selected.Copy, cfg, Environment.ProcessPath, _userSettings.EffectiveAutoMinutes);
                    AppendLog(LogLevel.Ok, $"Задание автообновления обновлено: {cfg.AutoMode}, {UserSettings.EveryText(_userSettings.EffectiveAutoMinutes)}.");
                }
                catch (Exception ex)
                {
                    // настройки уже сохранены — сообщаем только про задание
                    AppendLog(LogLevel.Error, "Настройки сохранены, но задание автообновления не обновлено: " + ex.Message);
                    MessageBox.Show(this, "Настройки сохранены, но задание автообновления не удалось перенастроить:\n" + ex.Message,
                        "Автообновление", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                _ = RefreshAutoAsync(_selected);
            }

            UpdateStatusCards();
            return true;
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

        private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveSettings();

        #region Pane widths

        private const double DefaultCopiesWidth = 290;
        private const double DefaultSettingsWidth = 410;

        private void RestorePaneWidths()
        {
            if (_userSettings.CopiesPaneWidth >= ColCopies.MinWidth && _userSettings.CopiesPaneWidth <= 520)
            {
                ColCopies.Width = new GridLength(_userSettings.CopiesPaneWidth);
            }

            if (_userSettings.SettingsPaneWidth >= ColSettings.MinWidth && _userSettings.SettingsPaneWidth <= 1600)
            {
                ColSettings.Width = new GridLength(_userSettings.SettingsPaneWidth);
            }
        }

        /// <summary>
        /// Ограничить максимумы под текущий размер окна: правой части — не меньше 700 px, логу — не меньше 300 px.
        /// (Минимумы у растягивающихся колонок задавать нельзя: при нехватке места разметка зацикливается.)
        /// </summary>
        private void Panes_SizeChanged(object sender, SizeChangedEventArgs e) => ClampPanes();

        private void ClampPanes()
        {
            double total = ((FrameworkElement)Content).ActualWidth;

            if (total > 0)
            {
                ColCopies.MaxWidth = Math.Max(ColCopies.MinWidth, Math.Min(520, total - 12 - 700));
            }

            double right = RightPanel.ActualWidth;

            if (right > 0)
            {
                ColSettings.MaxWidth = Math.Max(ColSettings.MinWidth, right - 12 - 300);
            }
        }

        private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SavePaneWidths();

        private void SplitCopies_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ColCopies.Width = new GridLength(DefaultCopiesWidth);
            SavePaneWidths();
        }

        private void SplitLog_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ColSettings.Width = new GridLength(DefaultSettingsWidth);
            SavePaneWidths();
        }

        /// <summary>Запомнить ширины (перечитывая настройки, чтобы не затереть остальное).</summary>
        private void SavePaneWidths()
        {
            try
            {
                UserSettings s = UserSettings.Load();
                s.CopiesPaneWidth = Math.Round(ColCopies.ActualWidth);
                s.SettingsPaneWidth = Math.Round(ColSettings.ActualWidth);
                s.Save();
                _userSettings.CopiesPaneWidth = s.CopiesPaneWidth;
                _userSettings.SettingsPaneWidth = s.SettingsPaneWidth;
            }
            catch
            {
                // ширины — удобство
            }
        }

        #endregion

        #region Do not overwrite

        private void BtnProtectFile_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            OpenFileDialog dlg = new OpenFileDialog
            {
                Title = "Файлы OsEngine, которые не нужно перезаписывать",
                Multiselect = true,
                InitialDirectory = DefaultPickDir()
            };

            if (dlg.ShowDialog(this) == true)
            {
                AddProtected(dlg.FileNames, false);
            }
        }

        private void BtnProtectFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            OpenFolderDialog dlg = new OpenFolderDialog
            {
                Title = "Папки OsEngine, которые не нужно перезаписывать",
                Multiselect = true,
                InitialDirectory = DefaultPickDir()
            };

            if (dlg.ShowDialog(this) == true)
            {
                AddProtected(dlg.FolderNames, true);
            }
        }

        private string DefaultPickDir()
        {
            string dir = Path.Combine(_selected.Root, @"project\OsEngine");
            return Directory.Exists(dir) ? dir : _selected.Root;
        }

        /// <summary>Пути внутри копии → маски «Не перезаписывать» (папка — всё внутри неё).</summary>
        private void AddProtected(IEnumerable<string> paths, bool folders)
        {
            string root = _selected.Root.TrimEnd('\\') + "\\";
            List<string> outside = new List<string>();

            foreach (string full in paths)
            {
                string f = Path.GetFullPath(full).TrimEnd('\\');

                if (!(f + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase) || f.Length <= root.Length - 1)
                {
                    outside.Add(full);
                    continue;
                }

                string rel = Path.GetRelativePath(_selected.Root, f).Replace('\\', '/');
                string pattern = folders ? rel + "/**" : rel;

                if (!_protected.Any(x => string.Equals(x.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    _protected.Add(new ExclusionItem(pattern));
                    SetDirty(true);
                }
            }

            if (outside.Count > 0)
            {
                MessageBox.Show(this, "Можно выбрать только файлы и папки внутри этой копии:\n" + _selected.Root
                                      + "\n\nНе добавлено:\n" + string.Join("\n", outside.Take(5)),
                    "Не перезаписывать", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnProtectRemove_Click(object sender, RoutedEventArgs e) => RemoveSelectedProtected();

        private void LstProtected_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Delete)
            {
                RemoveSelectedProtected();
            }
        }

        private void RemoveSelectedProtected()
        {
            List<ExclusionItem> sel = LstProtected.SelectedItems.Cast<ExclusionItem>().ToList();

            if (sel.Count == 0)
            {
                return;
            }

            foreach (ExclusionItem x in sel)
            {
                _protected.Remove(x);
            }

            SetDirty(true);
        }

        private void UpdateProtectedEmpty()
        {
            TxtProtectedEmpty.Visibility = _protected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnProtectRemove.IsEnabled = _protected.Count > 0;
        }

        #endregion

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

            await RunCopyUpdateAsync(_selected, mode, ChkForce.IsChecked == true, false);
        }

        /// <summary>Обновление копии из окна: вручную (кнопки) или автоматически (quick — по таймеру).</summary>
        private async Task RunCopyUpdateAsync(CopyItem item, UpdateMode mode, bool force, bool quick)
        {
            UpdaterConfig cfg = UpdaterConfig.Load(item.Copy);

            if (item == _selected)
            {
                AppendSeparator();
            }

            await RunOperationAsync(item, async (ct, sink) =>
            {
                UpdateLog log = new UpdateLog(item.Copy.LogPath, sink);
                using UpdateEngine engine = new UpdateEngine(item.Copy, cfg, log) { Force = force, QuickIfUpToDate = quick };
                return await engine.RunAsync(mode, ct);
            });

            await RefreshBehindAsync(item, true, item.LastHead);

            if (item == _selected)
            {
                UpdateStatusCards();
            }
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
                    AutoTaskService.Install(item.Copy, cfg, Environment.ProcessPath, _userSettings.EffectiveAutoMinutes);
                    AppendLog(LogLevel.Ok, $"Автообновление включено: {cfg.AutoMode}, {UserSettings.EveryText(_userSettings.EffectiveAutoMinutes)} и при входе в систему ({item.Copy.TaskName}).");
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
                MarkOwnWrite(_selected);
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Ошибка: " + ex.Message);
            }
        }

        #region My developments

        private void BtnPickRobot_Click(object sender, RoutedEventArgs e) => OpenPackages(PackageKind.Robot);

        private void BtnPickIndicator_Click(object sender, RoutedEventArgs e) => OpenPackages(PackageKind.Indicator);

        private void BtnPickFiles_Click(object sender, RoutedEventArgs e) => OpenPackages(PackageKind.Files);

        private void OpenPackages(PackageKind kind)
        {
            if (_selected == null || !ConfirmUnsaved())
            {
                return;
            }

            UpdaterConfig cfg = UpdaterConfig.Load(_selected.Copy);
            PackagesWindow w = new PackagesWindow(_selected.Copy, cfg, kind, _userSettings) { Owner = this };
            w.ShowDialog();

            _userSettings = UserSettings.Load();
            UpdateButtons();

            if (w.Changed)
            {
                LoadLogTail();
            }
        }

        private void BtnRepos_Click(object sender, RoutedEventArgs e)
        {
            ReposWindow w = new ReposWindow(_userSettings) { Owner = this };

            if (w.ShowDialog() == true)
            {
                _userSettings = UserSettings.Load();
                AppendLog(LogLevel.Ok, $"Мои аккаунты и репозитории сохранены: {_userSettings.Entries.Count} шт.");
                UpdateButtons();
                UpdateAppVersionText();

                if (w.InstallRequested != null)
                {
                    _pendingSelfUpdate = w.InstallRequested;
                    _ = SelfTickAsync();
                }
            }
        }

        private void UpdateDevButtons(bool has, bool busy)
        {
            int entries = _userSettings.Entries.Count;
            bool git = GitRunner.Available;
            bool on = has && !busy && git && entries > 0;
            string why = !git
                ? "Нужен Git for Windows (git-scm.com) — после установки перезапустите программу"
                : entries == 0 ? "Сначала добавьте свой аккаунт GitHub / GitFlic или репозиторий в «Мои аккаунты и репозитории…»" : null;

            BtnPickRobot.IsEnabled = on;
            BtnPickRobot.ToolTip = why ?? "Выбрать репозитории или отдельные файлы и поставить в папку роботов копии";
            BtnPickIndicator.IsEnabled = on;
            BtnPickIndicator.ToolTip = why ?? "Выбрать репозитории или отдельные файлы и поставить в папку индикаторов копии";
            BtnPickFiles.IsEnabled = on;
            BtnPickFiles.ToolTip = why ?? "Свои файлы в любую папку копии (можно поверх файлов OsEngine — обновление их не затрёт)";
            BtnRepos.IsEnabled = !busy;
        }

        #endregion

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
                MarkOwnWrite(item);
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
            UpdateDevButtons(has, busy);
            BtnCancel.IsEnabled = busy;
            BtnAddCopy.IsEnabled = !busy;
            BtnNewCopy.IsEnabled = !busy;
            BtnDeleteCopy.IsEnabled = has && !busy;
            CopiesList.IsEnabled = !busy;

            AutoTaskInfo a = _selected?.Auto;
            BtnAuto.Content = a?.Installed == true
                ? (a.Legacy ? "Перевести авто на exe" : "Выключить авто")
                : "Включить авто";
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_restarting)
            {
                return;
            }

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

    /// <summary>Строка списка «Не перезаписывать»: маска и её понятный вид.</summary>
    public sealed class ExclusionItem
    {
        public ExclusionItem(string pattern)
        {
            Pattern = pattern.Trim().Replace('\\', '/');
        }

        public string Pattern { get; }

        public bool IsFolder => Pattern.EndsWith("/**");

        public string Icon => IsFolder ? "📁" : (Pattern.Contains('*') || Pattern.Contains('?') ? "✱" : "📄");

        public string Display => IsFolder ? Pattern.Substring(0, Pattern.Length - 3) : Pattern;
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

        /// <summary>Последний коммит источника при последней проверке.</summary>
        public string LastHead { get; set; }
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
