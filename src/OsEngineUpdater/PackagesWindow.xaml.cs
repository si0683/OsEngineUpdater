using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Пакетный менеджер своих разработок: репозитории всех ваших аккаунтов слева,
    /// файлы выбранного репозитория справа. Тип установки (робот / индикатор / свои файлы) задан кнопкой,
    /// которой открыто окно. Changed = true — что-то установлено или удалено.
    /// </summary>
    public partial class PackagesWindow : Window
    {
        private readonly CopyInfo _copy;
        private readonly UpdaterConfig _cfg;
        private readonly PackageKind _kind;
        private UserSettings _settings;
        private readonly List<RepoRow> _allRows = new List<RepoRow>();
        private readonly ObservableCollection<RepoRow> _rows = new ObservableCollection<RepoRow>();
        private RepoRow _shown;
        private CancellationTokenSource _cts;
        private int _loadVersion;

        public bool Changed { get; private set; }

        public PackagesWindow(CopyInfo copy, UpdaterConfig cfg, PackageKind kind, UserSettings settings)
        {
            InitializeComponent();
            _copy = copy;
            _cfg = cfg;
            _kind = kind;
            _settings = settings;
            ReposList.ItemsSource = _rows;

            string what = kind switch { PackageKind.Robot => "робот", PackageKind.Indicator => "индикатор", _ => "свои файлы" };
            Title = "Выбрать " + what;
            TxtTitle.Text = $"Выбрать {what} → {copy.Name}";
            TxtSubtitle.Text = kind switch
            {
                PackageKind.Robot => $"Ставится в {cfg.EffectiveRobotsDir}\\<имя репозитория>\\…",
                PackageKind.Indicator => $"Ставится в {cfg.EffectiveIndicatorsDir}\\<имя репозитория>\\…",
                _ => "Файлы раскладываются в указанную папку копии со структурой репозитория и не затираются обновлением OsEngine"
            };

            if (kind == PackageKind.Files)
            {
                PanelFilesTarget.Visibility = Visibility.Visible;
                TxtFilesTarget.Text = cfg.EffectiveFilesDir;
            }

            ShowFiles(null);
        }

        private string FilesTargetRel => PackageManager.Rel(_copy,
            PackageManager.TargetDir(_copy, _cfg, PackageKind.Files, "", TxtFilesTarget.Text));

        #region Repos

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadReposAsync();
        }

        private async Task LoadReposAsync()
        {
            int version = ++_loadVersion;
            DisposeCheckouts();
            _allRows.Clear();
            _rows.Clear();
            ShowFiles(null);
            TxtReposInfo.Text = "загружаю список репозиториев…";

            if (!GitRunner.Available)
            {
                AppendLog(LogLevel.Error, "Git не найден. Установите Git for Windows (git-scm.com) и перезапустите программу.");
            }

            // репозитории всех аккаунтов параллельно
            var tasks = _settings.Entries
                .Where(en => SourceEntry.Parse(en.Url).Owner != null)
                .Select(async en =>
                {
                    try
                    {
                        return (en, repos: await RepoCatalog.ListAsync(en, CancellationToken.None), error: (string)null);
                    }
                    catch (Exception ex)
                    {
                        return (en, repos: new List<RepoInfo>(), error: ex.Message);
                    }
                })
                .ToList();

            var results = await Task.WhenAll(tasks);

            if (version != _loadVersion)
            {
                return;
            }

            List<InstalledPackage> installed = PackageManager.LoadInstalled(_copy).Where(p => p.Kind == _kind).ToList();

            foreach (var r in results)
            {
                if (r.error != null)
                {
                    AppendLog(LogLevel.Error, $"{r.en.Title}: {r.error}");
                }

                foreach (RepoInfo repo in r.repos)
                {
                    if (_allRows.Any(x => x.Repo.Id == repo.Id))
                    {
                        continue;
                    }

                    _allRows.Add(new RepoRow(this, repo, FindInstalled(installed, repo.Id)));
                }
            }

            // установлено раньше, но репозитория больше нет в списке — даём обновить или удалить
            foreach (InstalledPackage p in installed.Where(p => _allRows.All(x => x.Installed != p)))
            {
                RepoInfo repo = new RepoInfo
                {
                    Name = p.Name,
                    CloneUrl = p.Url,
                    WebUrl = p.Url,
                    Branch = p.Branch,
                    Provider = SourceEntry.Parse(p.Url).Provider,
                    Entry = _settings.Entries.FirstOrDefault(en => SourceEntry.Parse(en.Url).Owner != null
                                                                   && string.Equals(SourceEntry.Parse(en.Url).Owner, SourceEntry.Parse(p.Url).Owner, StringComparison.OrdinalIgnoreCase))
                };
                _allRows.Add(new RepoRow(this, repo, p) { Orphan = true });
            }

            ApplyFilter();
            TxtReposInfo.Text = _allRows.Count == 0
                ? ""
                : $"репозиториев: {_allRows.Count}, установлено: {_allRows.Count(x => x.Installed != null)}";

            // обновления для установленных (ls-remote)
            foreach (RepoRow row in _allRows.Where(x => x.Installed != null).ToList())
            {
                try
                {
                    row.RemoteHead = await GitRunner.RemoteHeadAsync(row.Repo, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    row.RemoteError = ex.Message;
                }

                if (version != _loadVersion)
                {
                    return;
                }

                row.UpdateStatus();
            }
        }

        private InstalledPackage FindInstalled(List<InstalledPackage> installed, string repoId)
        {
            if (_kind != PackageKind.Files)
            {
                return installed.FirstOrDefault(p => p.RepoId == repoId);
            }

            string target = SafeFilesTarget();
            return installed.FirstOrDefault(p => p.RepoId == repoId && string.Equals(p.TargetDir, target, StringComparison.OrdinalIgnoreCase))
                   ?? installed.FirstOrDefault(p => p.RepoId == repoId);
        }

        private string SafeFilesTarget()
        {
            try
            {
                return FilesTargetRel;
            }
            catch
            {
                return "";
            }
        }

        private void ApplyFilter()
        {
            string q = TxtSearch.Text.Trim();
            _rows.Clear();

            foreach (RepoRow r in _allRows
                         .OrderByDescending(x => x.Installed != null)
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    _rows.Add(r);
                }
            }

            TxtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Text = _settings.Entries.Count == 0
                ? "Список пуст.\nДобавьте свой аккаунт GitHub / GitFlic или репозиторий кнопкой «Мои аккаунты и репозитории…»."
                : (q.Length > 0 ? "Ничего не найдено." : "Репозиториев нет.");
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private async void BtnReload_Click(object sender, RoutedEventArgs e) => await LoadReposAsync();

        private void TxtFilesTarget_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            List<InstalledPackage> installed = PackageManager.LoadInstalled(_copy).Where(p => p.Kind == _kind).ToList();

            foreach (RepoRow r in _allRows)
            {
                r.SetInstalled(FindInstalled(installed, r.Repo.Id));
            }
        }

        #endregion

        #region Files tree

        private async void ReposList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RepoRow row = ReposList.SelectedItem as RepoRow;

            if (row == null)
            {
                return;
            }

            ShowFiles(row);

            if (row.Checkout == null && row.FetchError == null && !row.Fetching)
            {
                await FetchRowAsync(row);

                if (_shown == row)
                {
                    ShowFiles(row);
                }
            }
        }

        private async Task FetchRowAsync(RepoRow row)
        {
            row.Fetching = true;
            row.UpdateStatus();

            try
            {
                row.Checkout = await PackageManager.FetchAsync(row.Repo, CancellationToken.None);
                row.BuildTree();
                row.RemoteHead ??= row.Checkout.Commit;
            }
            catch (Exception ex)
            {
                row.FetchError = ex.Message;
                AppendLog(LogLevel.Error, $"{row.Name}: {ex.Message}");
            }

            row.Fetching = false;
            row.UpdateStatus();
        }

        private void ShowFiles(RepoRow row)
        {
            _shown = row;
            FilesTree.ItemsSource = row?.Tree;
            BtnAll.IsEnabled = BtnNone.IsEnabled = row?.Tree != null;

            if (row == null)
            {
                TxtFilesTitle.Text = "Файлы";
                TxtFilesInfo.Text = "";
                TxtFilesEmpty.Visibility = Visibility.Visible;
                return;
            }

            TxtFilesTitle.Text = "Файлы: " + row.Name;
            TxtFilesEmpty.Visibility = row.Tree == null ? Visibility.Visible : Visibility.Collapsed;

            if (row.Fetching)
            {
                TxtFilesInfo.Text = "скачиваю репозиторий…";
                TxtFilesEmpty.Text = "Скачиваю… Для закрытого репозитория без токена Git может показать окно входа.";
            }
            else if (row.FetchError != null)
            {
                TxtFilesInfo.Text = "";
                TxtFilesEmpty.Text = "Не удалось скачать:\n" + row.FetchError;
            }
            else
            {
                UpdateFilesInfo();
            }
        }

        internal void OnTreeChanged(RepoRow row)
        {
            if (row == _shown)
            {
                UpdateFilesInfo();
            }
        }

        private void UpdateFilesInfo()
        {
            if (_shown?.Tree == null)
            {
                return;
            }

            int total = _shown.Checkout.Files.Count;
            int sel = _shown.SelectedFiles().Count;
            TxtFilesInfo.Text = sel == total
                ? $"отмечены все файлы ({total}) — " + (_kind == PackageKind.Files ? "ставится целиком" : "ставится целиком как git-репозиторий (commit/push из VS)")
                : $"отмечено файлов: {sel} из {total} — будут скопированы без git";
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e) => _shown?.SetAll(true);

        private void BtnNone_Click(object sender, RoutedEventArgs e) => _shown?.SetAll(false);

        #endregion

        #region Install / uninstall

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            List<RepoRow> rows = _allRows.Where(r => r.IsChecked).ToList();

            if (rows.Count == 0)
            {
                AppendLog(LogLevel.Warn, "Отметьте галочками репозитории (слева), которые нужно установить.");
                return;
            }

            string filesTarget = TxtFilesTarget.Text;

            if (_kind == PackageKind.Files)
            {
                try
                {
                    PackageManager.TargetDir(_copy, _cfg, PackageKind.Files, "", filesTarget);
                }
                catch (Exception ex)
                {
                    AppendLog(LogLevel.Error, ex.Message);
                    return;
                }
            }

            await RunAsync(async log =>
            {
                int ok = 0;

                foreach (RepoRow r in rows)
                {
                    IReadOnlyCollection<string> selected = Dispatcher.Invoke(() => r.SelectionForInstall());
                    PackageResult res;

                    if (PackageManager.UseGitMode(_copy, _cfg, r.Repo, _kind, selected))
                    {
                        // целиком — рабочий git-репозиторий (clone / pull)
                        if (selected != null)
                        {
                            log.Warn($"{r.Name}: папка уже git-репозиторий — обновляется целиком (git pull), выбор файлов не применяется.");
                        }

                        res = await PackageManager.InstallGitAsync(_copy, _cfg, r.Repo, _kind, log, CancellationToken.None);
                    }
                    else
                    {
                        if (r.Checkout == null)
                        {
                            await Dispatcher.InvokeAsync(() => FetchRowAsync(r)).Task.Unwrap();
                        }

                        if (r.Checkout == null)
                        {
                            continue;
                        }

                        res = PackageManager.Install(_copy, _cfg, r.Checkout, _kind, filesTarget, selected, log,
                            msg => Dispatcher.Invoke(() => Confirm(msg)));
                    }

                    if (res.Done)
                    {
                        ok++;
                    }
                }

                if (ok > 0)
                {
                    Changed = true;

                    if (_kind == PackageKind.Files && !string.Equals(_cfg.EffectiveFilesDir, filesTarget.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _cfg.FilesDir = filesTarget.Trim();
                        _cfg.Save(_copy);
                    }

                    VsRefresher.Run(_copy, log, false);
                }
            });
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            List<RepoRow> rows = _allRows.Where(r => r.IsChecked && r.Installed != null).ToList();

            if (rows.Count == 0)
            {
                AppendLog(LogLevel.Warn, "Отметьте галочками установленные репозитории, которые нужно удалить из копии.");
                return;
            }

            if (MessageBox.Show(this, "Удалить из копии:\n  " + string.Join("\n  ", rows.Select(r => r.Name)) + "\n\nРепозитории не затрагиваются.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            await RunAsync(log =>
            {
                foreach (RepoRow r in rows)
                {
                    PackageManager.Uninstall(_copy, r.Installed, log, msg => Dispatcher.Invoke(() => Confirm(msg)));
                }

                Changed = true;
                VsRefresher.Run(_copy, log, false);
                return Task.CompletedTask;
            });
        }

        private async void BtnRepos_Click(object sender, RoutedEventArgs e)
        {
            ReposWindow w = new ReposWindow(_settings) { Owner = this };

            if (w.ShowDialog() == true)
            {
                _settings = UserSettings.Load();
                await LoadReposAsync();
            }
        }

        private async Task RunAsync(Func<UpdateLog, Task> body)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            UpdateLog log = new UpdateLog(_copy.LogPath, (l, m) => Dispatcher.BeginInvoke(() => AppendLog(l, m)));

            try
            {
                await Task.Run(() => body(log));
            }
            catch (Exception ex)
            {
                AppendLog(LogLevel.Error, "Ошибка: " + ex.Message);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetBusy(false);
            }

            // обновить статусы установленного
            List<InstalledPackage> installed = PackageManager.LoadInstalled(_copy).Where(p => p.Kind == _kind).ToList();

            foreach (RepoRow r in _allRows)
            {
                r.IsChecked = false;
                r.SetInstalled(FindInstalled(installed, r.Repo.Id));
            }

            TxtReposInfo.Text = $"репозиториев: {_allRows.Count}, установлено: {_allRows.Count(x => x.Installed != null)}";
        }

        private bool Confirm(string msg)
        {
            return MessageBox.Show(this, msg, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void SetBusy(bool busy)
        {
            BtnInstall.IsEnabled = !busy;
            BtnUninstall.IsEnabled = !busy;
            BtnRepos.IsEnabled = !busy;
            BtnReload.IsEnabled = !busy;
            ReposList.IsEnabled = !busy;
            FilesTree.IsEnabled = !busy;
            BtnAll.IsEnabled = BtnNone.IsEnabled = !busy && _shown?.Tree != null;
            BtnClose.IsEnabled = !busy;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_cts != null)
            {
                e.Cancel = true;
                return;
            }

            DisposeCheckouts();
        }

        private void DisposeCheckouts()
        {
            foreach (RepoRow r in _allRows)
            {
                r.Checkout?.Dispose();
            }
        }

        internal void AppendLog(LogLevel level, string message)
        {
            string prefix = level switch { LogLevel.Warn => "! ", LogLevel.Error => "✖ ", LogLevel.Ok => "✔ ", _ => "" };
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}{Environment.NewLine}");
            TxtLog.ScrollToEnd();
        }

        internal Brush Brush(string key) => (Brush)FindResource(key);

        #endregion
    }

    /// <summary>Репозиторий в списке окна выбора.</summary>
    public sealed class RepoRow : INotifyPropertyChanged
    {
        private readonly PackagesWindow _owner;

        public RepoRow(PackagesWindow owner, RepoInfo repo, InstalledPackage installed)
        {
            _owner = owner;
            Repo = repo;
            Installed = installed;
            UpdateStatus();
        }

        public RepoInfo Repo { get; }
        public InstalledPackage Installed { get; private set; }
        public bool Orphan { get; init; }
        public RepoCheckout Checkout { get; set; }
        public ObservableCollection<FileNode> Tree { get; private set; }
        public bool Fetching { get; set; }
        public string FetchError { get; set; }
        public string RemoteHead { get; set; }
        public string RemoteError { get; set; }

        public string Name => Repo.Name;
        public string PrivateMark => Repo.Private ? "  закрытый" : "";

        public string Origin
        {
            get
            {
                string host = Repo.Provider switch { GitProvider.GitHub => "GitHub", GitProvider.GitFlic => "GitFlic", _ => "git" };
                string desc = string.IsNullOrWhiteSpace(Repo.Description) ? "" : " · " + Repo.Description;
                return Orphan ? $"{Repo.WebUrl} (нет в «Мои аккаунты»)" : $"{host} · {Repo.Owner}{desc}";
            }
        }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnChanged(); }
        }

        private string _status;
        public string Status
        {
            get => _status;
            private set { _status = value; OnChanged(); }
        }

        private Brush _statusBrush;
        public Brush StatusBrush
        {
            get => _statusBrush;
            private set { _statusBrush = value; OnChanged(); }
        }

        public void SetInstalled(InstalledPackage p)
        {
            Installed = p;
            UpdateStatus();

            if (Tree != null)
            {
                ApplyInstalledSelection();
            }
        }

        public void UpdateStatus()
        {
            if (Fetching)
            {
                Set("скачиваю…", "FgDim");
            }
            else if (Installed == null)
            {
                Set(FetchError != null ? "нет доступа: " + FetchError : "не установлен", FetchError != null ? "Bad" : "FgDim");
            }
            else
            {
                string part = Installed.Git ? "git-репозиторий" : (Installed.Selected == null ? "целиком" : $"файлов: {Installed.Selected.Count}");
                string when = DateTime.TryParse(Installed.InstalledUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt)
                    ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                    : "";

                if (RemoteHead != null && !string.Equals(RemoteHead, Installed.Commit, StringComparison.OrdinalIgnoreCase))
                {
                    Set($"есть обновление ({UpdateEngine.Short(Installed.Commit)} → {UpdateEngine.Short(RemoteHead)}), {part}", "Warn");
                }
                else if (RemoteError != null)
                {
                    Set($"установлен {UpdateEngine.Short(Installed.Commit)}, {part} · не проверить обновление: {RemoteError}", "Warn");
                }
                else
                {
                    Set($"установлен {UpdateEngine.Short(Installed.Commit)} · {when} · {part}", "Good");
                }
            }
        }

        private void Set(string text, string brush)
        {
            Status = text;
            StatusBrush = _owner.Brush(brush);
        }

        #region Tree

        public void BuildTree()
        {
            ObservableCollection<FileNode> roots = new ObservableCollection<FileNode>();

            foreach (string path in Checkout.Files)
            {
                string[] parts = path.Split('/');
                ObservableCollection<FileNode> level = roots;
                FileNode parent = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    bool isFile = i == parts.Length - 1;
                    FileNode node = level.FirstOrDefault(n => n.Name == parts[i] && n.IsDir != isFile);

                    if (node == null)
                    {
                        node = new FileNode(this, parts[i], string.Join("/", parts.Take(i + 1)), !isFile, parent);
                        level.Add(node);
                    }

                    parent = node;
                    level = node.Children;
                }
            }

            Sort(roots);

            foreach (FileNode n in roots.Where(n => n.IsDir))
            {
                n.IsExpanded = roots.Count < 6;
            }

            Tree = roots;
            ApplyInstalledSelection();
        }

        private static void Sort(ObservableCollection<FileNode> nodes)
        {
            List<FileNode> sorted = nodes.OrderByDescending(n => n.IsDir).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
            nodes.Clear();

            foreach (FileNode n in sorted)
            {
                nodes.Add(n);
                Sort(n.Children);
            }
        }

        /// <summary>Отметки по прошлой установке; если не установлен — все файлы.</summary>
        private void ApplyInstalledSelection()
        {
            HashSet<string> sel = Installed?.Selected == null ? null : new HashSet<string>(Installed.Selected, StringComparer.OrdinalIgnoreCase);
            _suppress = true;

            foreach (FileNode leaf in Leaves(Tree))
            {
                leaf.SetLeaf(sel == null || sel.Contains(leaf.Path));
            }

            foreach (FileNode root in Tree)
            {
                root.RecalcDown();
            }

            _suppress = false;
            _owner.OnTreeChanged(this);
        }

        public void SetAll(bool value)
        {
            foreach (FileNode n in Tree)
            {
                n.IsChecked = value;
            }
        }

        private bool _suppress;

        internal void OnNodeChanged()
        {
            if (_suppress)
            {
                return;
            }

            // отметили файлы — значит репозиторий ставим; сняли всё — не ставим
            IsChecked = SelectedFiles().Count > 0;
            _owner.OnTreeChanged(this);
        }

        public List<string> SelectedFiles()
        {
            return Tree == null ? new List<string>() : Leaves(Tree).Where(l => l.IsChecked == true).Select(l => l.Path).ToList();
        }

        /// <summary>Выбор для установки: null — весь репозиторий.</summary>
        public IReadOnlyCollection<string> SelectionForInstall()
        {
            if (Tree == null)
            {
                return Installed?.Selected;
            }

            List<string> sel = SelectedFiles();
            return sel.Count == Checkout.Files.Count ? null : sel;
        }

        private static IEnumerable<FileNode> Leaves(IEnumerable<FileNode> nodes)
        {
            foreach (FileNode n in nodes)
            {
                if (n.IsDir)
                {
                    foreach (FileNode l in Leaves(n.Children)) yield return l;
                }
                else
                {
                    yield return n;
                }
            }
        }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Файл или папка в дереве репозитория (с галочкой, у папок — три состояния).</summary>
    public sealed class FileNode : INotifyPropertyChanged
    {
        private readonly RepoRow _row;
        private readonly FileNode _parent;
        private bool? _isChecked = true;

        public FileNode(RepoRow row, string name, string path, bool isDir, FileNode parent)
        {
            _row = row;
            Name = name;
            Path = path;
            IsDir = isDir;
            _parent = parent;
        }

        public string Name { get; }
        public string Path { get; }
        public bool IsDir { get; }
        public ObservableCollection<FileNode> Children { get; } = new ObservableCollection<FileNode>();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnChanged(); }
        }

        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                bool v = value ?? true;
                SetDown(v);
                _parent?.RecalcUp();
                _row.OnNodeChanged();
            }
        }

        internal void SetLeaf(bool v)
        {
            _isChecked = v;
            OnChanged(nameof(IsChecked));
        }

        private void SetDown(bool v)
        {
            _isChecked = v;
            OnChanged(nameof(IsChecked));

            foreach (FileNode c in Children)
            {
                c.SetDown(v);
            }
        }

        private void RecalcUp()
        {
            _isChecked = Combined();
            OnChanged(nameof(IsChecked));
            _parent?.RecalcUp();
        }

        /// <summary>Пересчёт состояний папок снизу вверх.</summary>
        internal void RecalcDown()
        {
            if (!IsDir)
            {
                return;
            }

            foreach (FileNode c in Children)
            {
                c.RecalcDown();
            }

            _isChecked = Combined();
            OnChanged(nameof(IsChecked));
        }

        private bool? Combined()
        {
            if (Children.All(c => c.IsChecked == true)) return true;
            if (Children.All(c => c.IsChecked == false)) return false;
            return null;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
