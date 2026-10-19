using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Запись «Мои аккаунты и репозитории»: GitHub и/или GitFlic. В settings.json это две SourceEntry с общим Group.
    /// </summary>
    public sealed class AccountGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public SourceEntry GitHub { get; set; }
        public SourceEntry GitFlic { get; set; }

        private static bool Filled(SourceEntry e) => e != null && !string.IsNullOrWhiteSpace(e.Url);

        public bool IsEmpty => !Filled(GitHub) && !Filled(GitFlic);

        public string Title
        {
            get
            {
                List<string> names = new[] { GitHub, GitFlic }.Where(Filled)
                    .Select(e => e.RepoName == null ? e.Owner ?? e.Url : $"{e.Owner}/{e.RepoName}")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return names.Count == 0 ? "новая запись" : string.Join(" / ", names);
            }
        }

        public string Subtitle
        {
            get
            {
                List<string> parts = new List<string>();

                if (Filled(GitHub)) parts.Add("GitHub" + (GitHub.HasToken ? " · токен" : ""));
                if (Filled(GitFlic)) parts.Add("GitFlic" + (GitFlic.HasToken ? " · токен" : ""));

                return parts.Count == 0 ? "заполните GitHub или GitFlic справа" : string.Join("  +  ", parts);
            }
        }
    }

    /// <summary>
    /// «Мои аккаунты и репозитории» — общие для всех копий. Слева записи, справа GitHub и GitFlic выбранной записи.
    /// DialogResult = true — настройки сохранены.
    /// </summary>
    public partial class ReposWindow : Window
    {
        private readonly UserSettings _settings;
        private readonly ObservableCollection<AccountGroup> _groups;

        /// <summary>Записи с других git-серверов (из старых версий): в окне не показываются, сохраняются как есть.</summary>
        private readonly List<SourceEntry> _others;

        private AccountGroup _current;
        private bool _dirty;
        private ReleaseInfo _found;

        /// <summary>Пользователь попросил установить найденную новую версию программы.</summary>
        public ReleaseInfo InstallRequested { get; private set; }

        public ReposWindow(UserSettings settings)
        {
            InitializeComponent();
            _settings = settings.Clone();
            _others = _settings.Entries.Where(e => e.Provider == GitProvider.Other).ToList();
            _groups = new ObservableCollection<AccountGroup>(Group(_settings.Entries.Where(e => e.Provider != GitProvider.Other)));

            // пустой список — сразу показываем пустую запись, чтобы было куда вписать аккаунт
            if (_groups.Count == 0)
            {
                _groups.Add(new AccountGroup());
            }

            EntriesList.ItemsSource = _groups;

            TxtGit.Text = GitRunner.Available ? "Git: " + GitRunner.GitPath : "Git не найден — установите Git for Windows (git-scm.com)";

            FillSelfUpdate();
            EntriesList.SelectedIndex = 0;
        }

        /// <summary>
        /// SourceEntry → записи окна: по Group; записи старых версий (без Group) — GitHub и GitFlic с одинаковым логином вместе.
        /// </summary>
        private static List<AccountGroup> Group(IEnumerable<SourceEntry> entries)
        {
            List<AccountGroup> groups = new List<AccountGroup>();

            foreach (SourceEntry e in entries)
            {
                bool flic = e.Provider == GitProvider.GitFlic;
                AccountGroup g = string.IsNullOrEmpty(e.Group)
                    ? groups.FirstOrDefault(x => x.Id.StartsWith("legacy:") && (flic ? x.GitFlic : x.GitHub) == null
                                                 && string.Equals(x.Id.Substring(7), e.Owner, StringComparison.OrdinalIgnoreCase))
                    : groups.FirstOrDefault(x => x.Id == e.Group && (flic ? x.GitFlic : x.GitHub) == null);

                if (g == null)
                {
                    g = new AccountGroup { Id = string.IsNullOrEmpty(e.Group) ? "legacy:" + e.Owner : e.Group };

                    // такой Group уже занят (два GitHub в одной записи) — отдельная запись
                    if (groups.Any(x => x.Id == g.Id))
                    {
                        g.Id = Guid.NewGuid().ToString("N");
                    }

                    groups.Add(g);
                }

                if (flic)
                {
                    g.GitFlic = e;
                }
                else
                {
                    g.GitHub = e;
                }
            }

            foreach (AccountGroup g in groups.Where(x => x.Id.StartsWith("legacy:")))
            {
                g.Id = Guid.NewGuid().ToString("N");
            }

            return groups;
        }

        private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EntriesList.SelectedItem is AccountGroup g && g != _current)
            {
                ShowGroup(g);
            }
            else if (EntriesList.SelectedItem == null)
            {
                ShowGroup(null);
            }
        }

        private void ShowGroup(AccountGroup g)
        {
            _current = g;
            RecordPanel.IsEnabled = g != null;
            BtnRemove.IsEnabled = g != null;
            TxtRecordTitle.Text = g == null ? "Запись" : "Запись: " + g.Title;

            BlockGitHub.Load(GitProvider.GitHub, g?.GitHub);
            BlockGitFlic.Load(GitProvider.GitFlic, g?.GitFlic);

            if (g != null)
            {
                // блок создаёт пустую запись, если её не было
                g.GitHub = BlockGitHub.Entry;
                g.GitFlic = BlockGitFlic.Entry;
            }
        }

        private void Block_Changed(object sender, EventArgs e)
        {
            _dirty = true;

            if (_current != null)
            {
                TxtRecordTitle.Text = "Запись: " + _current.Title;
                EntriesList.Items.Refresh();
            }
        }

        #region Self update

        private bool _loadingSelf;

        private void FillSelfUpdate()
        {
            _loadingSelf = true;
            ChkSelfUpdate.IsChecked = _settings.SelfUpdateEnabled;
            List<ComboBoxItem> items = CmbSelfMinutes.Items.Cast<ComboBoxItem>().ToList();
            CmbSelfMinutes.SelectedItem = items.OrderBy(i => Math.Abs(int.Parse((string)i.Tag) - _settings.SelfUpdateMinutes)).First();
            CmbSelfMinutes.IsEnabled = _settings.SelfUpdateEnabled;
            _loadingSelf = false;

            (bool can, string why) = SelfUpdater.CanSelfUpdate();
            string last = DateTime.TryParse(_settings.LastSelfCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt)
                ? $" · проверено {dt.ToLocalTime():dd.MM HH:mm}"
                : "";
            TxtSelfInfo.Text = $"Установлена версия {SelfUpdater.CurrentVersionText} · источник github.com/{_settings.SelfUpdateRepo}{last}"
                               + (can ? "" : $"\nСамообновление недоступно: {why}");
        }

        private void SelfSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSelf || !IsLoaded && sender == CmbSelfMinutes)
            {
                return;
            }

            _settings.SelfUpdateEnabled = ChkSelfUpdate.IsChecked == true;
            _settings.SelfUpdateMinutes = int.Parse((string)((ComboBoxItem)CmbSelfMinutes.SelectedItem).Tag);
            CmbSelfMinutes.IsEnabled = _settings.SelfUpdateEnabled;
            _dirty = true;
        }

        private async void BtnSelfCheck_Click(object sender, RoutedEventArgs e)
        {
            BtnSelfCheck.IsEnabled = false;
            BtnSelfInstall.Visibility = Visibility.Collapsed;
            SetSelfStatus("проверяю…", "FgDim");

            try
            {
                ReleaseInfo r = await SelfUpdater.GetLatestAsync(_settings.SelfUpdateRepo, CancellationToken.None);
                SelfUpdater.MarkChecked();
                _settings.LastSelfCheckUtc = DateTime.UtcNow.ToString("o");

                if (r == null)
                {
                    SetSelfStatus("В репозитории ещё нет релизов с OsEngineUpdater.exe.", "FgDim");
                }
                else if (SelfUpdater.IsNewer(r))
                {
                    _found = r;
                    (bool can, string why) = SelfUpdater.CanSelfUpdate();
                    SetSelfStatus($"Доступна новая версия {SelfUpdater.Short(r.Version)}." + (can ? "" : " Установить автоматически нельзя: " + why), "Warn");
                    BtnSelfInstall.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    SetSelfStatus($"Установлена последняя версия (релиз {SelfUpdater.Short(r.Version)}).", "Good");
                }
            }
            catch (Exception ex)
            {
                SetSelfStatus("Не удалось проверить: " + ex.Message, "Bad");
            }

            BtnSelfCheck.IsEnabled = true;
        }

        private void BtnSelfInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_found == null)
            {
                return;
            }

            InstallRequested = _found;

            if (_dirty)
            {
                // «Сохранить» само закрывает окно с DialogResult = true; не сохранилось — не устанавливаем
                BtnSave_Click(sender, e);

                if (_dirty)
                {
                    InstallRequested = null;
                }

                return;
            }

            DialogResult = true;
        }

        private void SetSelfStatus(string text, string brush)
        {
            TxtSelfStatus.Text = text;
            TxtSelfStatus.Foreground = (Brush)FindResource(brush);
        }

        #endregion

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            AccountGroup g = new AccountGroup();
            _groups.Add(g);
            EntriesList.SelectedItem = g;
            _dirty = true;
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                return;
            }

            if (!_current.IsEmpty
                && MessageBox.Show(this, $"Убрать «{_current.Title}» из списка?\n\nУже установленное в копиях не удаляется — для этого есть кнопка «Удалить из копии» в окне выбора.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            int i = _groups.IndexOf(_current);
            _groups.Remove(_current);
            _current = null;
            _dirty = true;
            EntriesList.SelectedIndex = Math.Min(i, _groups.Count - 1);

            if (_groups.Count == 0)
            {
                ShowGroup(null);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            foreach (AccountGroup g in _groups)
            {
                string error = AccountBlock.Validate(GitProvider.GitHub, g.GitHub) ?? AccountBlock.Validate(GitProvider.GitFlic, g.GitFlic);

                if (error != null)
                {
                    EntriesList.SelectedItem = g;
                    MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            List<SourceEntry> all = new List<SourceEntry>();

            foreach (AccountGroup g in _groups)
            {
                foreach (SourceEntry entry in new[] { g.GitHub, g.GitFlic })
                {
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.Url))
                    {
                        entry.Group = g.Id;
                        all.Add(entry);
                    }
                }
            }

            all.AddRange(_others);
            _settings.Entries = all;

            try
            {
                _settings.Save();
                _dirty = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_dirty && DialogResult != true
                && MessageBox.Show(this, "Закрыть без сохранения?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        }
    }
}
