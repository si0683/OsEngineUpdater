using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// «Мои аккаунты и репозитории» — общие для всех копий: аккаунт GitHub, аккаунт GitFlic (можно оба сразу)
    /// и список отдельных репозиториев. Всё хранится списком SourceEntry, как и раньше.
    /// DialogResult = true — настройки сохранены.
    /// </summary>
    public partial class ReposWindow : Window
    {
        private readonly UserSettings _settings;
        private readonly ObservableCollection<SourceEntry> _entries;
        private SourceEntry _current;
        private bool _loading;
        private bool _dirty;
        private ReleaseInfo _found;

        /// <summary>Пользователь попросил установить найденную новую версию программы.</summary>
        public ReleaseInfo InstallRequested { get; private set; }

        public ReposWindow(UserSettings settings)
        {
            InitializeComponent();
            _settings = settings.Clone();

            // первый аккаунт GitHub и первый GitFlic — в свои блоки, остальное — в список отдельных репозиториев
            SourceEntry gitHub = _settings.Entries.FirstOrDefault(x => x.IsAccount && x.Provider == GitProvider.GitHub);
            SourceEntry gitFlic = _settings.Entries.FirstOrDefault(x => x.IsAccount && x.Provider == GitProvider.GitFlic);
            BlockGitHub.Load(GitProvider.GitHub, gitHub);
            BlockGitFlic.Load(GitProvider.GitFlic, gitFlic);

            _entries = new ObservableCollection<SourceEntry>(_settings.Entries.Where(x => x != gitHub && x != gitFlic));
            EntriesList.ItemsSource = _entries;

            TxtGit.Text = GitRunner.Available ? "Git: " + GitRunner.GitPath : "Git не найден — установите Git for Windows (git-scm.com)";

            FillSelfUpdate();

            if (_entries.Count > 0)
            {
                EntriesList.SelectedIndex = 0;
            }
            else
            {
                ShowEntry(null);
            }
        }

        private void Block_Changed(object sender, EventArgs e)
        {
            _dirty = true;
        }

        private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowEntry(EntriesList.SelectedItem as SourceEntry);
        }

        private void ShowEntry(SourceEntry entry)
        {
            _current = entry;
            _loading = true;

            FormGrid.IsEnabled = entry != null;
            BtnRemove.IsEnabled = entry != null;
            TxtListEmpty.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (entry == null)
            {
                PanelHowToken.Visibility = Visibility.Collapsed;
            }
            TxtTest.Text = "";
            TxtUrl.Text = entry?.Url ?? "";
            TxtBranch.Text = entry?.Branch ?? "";
            PwdToken.Password = entry?.Token ?? "";

            _loading = false;
            UpdateHints();
        }

        private void Form_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _current == null)
            {
                return;
            }

            _current.Url = TxtUrl.Text.Trim();
            _current.Branch = TxtBranch.Text.Trim();

            if (sender == PwdToken)
            {
                _current.Token = PwdToken.Password;
            }

            _dirty = true;
            EntriesList.Items.Refresh();
            UpdateHints();
        }

        private void UpdateHints()
        {
            if (_current == null || string.IsNullOrWhiteSpace(_current.Url))
            {
                TxtDetected.Text = "";
                TxtTokenHint.Text = "";
                BtnHowToken.Content = "Как получить токен?";
                TokenHelp.Fill(TxtHowToken, BtnOpenTokenPage, BtnOpenTokenPage2, GitProvider.GitHub);
                LblBranch.Visibility = TxtBranch.Visibility = Visibility.Visible;
                return;
            }

            bool account = _current.IsAccount;
            LblBranch.Visibility = TxtBranch.Visibility = account ? Visibility.Collapsed : Visibility.Visible;

            TxtDetected.Text = _current.Provider switch
            {
                GitProvider.GitHub when account => $"GitHub, аккаунт {_current.Owner} — в окне выбора будут все его репозитории",
                GitProvider.GitFlic when account => $"GitFlic, аккаунт {_current.Owner} — в окне выбора будут все его проекты",
                GitProvider.GitHub => $"GitHub, репозиторий {_current.Owner}/{_current.RepoName}",
                GitProvider.GitFlic when _current.RepoName != null => $"GitFlic, проект {_current.Owner}/{_current.RepoName}",
                GitProvider.GitFlic => "GitFlic: укажите https://gitflic.ru/user/<логин> или https://gitflic.ru/project/<логин>/<проект>",
                _ => "Репозиторий на другом git-сервере"
            };

            GitProvider howFor = _current.Provider == GitProvider.GitFlic ? GitProvider.GitFlic : GitProvider.GitHub;
            BtnHowToken.Content = TokenHelp.LinkText(howFor);
            TokenHelp.Fill(TxtHowToken, BtnOpenTokenPage, BtnOpenTokenPage2, howFor);

            TxtTokenHint.Text = _current.Provider switch
            {
                GitProvider.GitHub => "Необязательно: нужен, чтобы видеть и ставить закрытые репозитории.",
                GitProvider.GitFlic when account => "Обязателен: без токена GitFlic не отдаёт список проектов.",
                GitProvider.GitFlic => "Нужен для закрытого проекта (или войдёте через окно Git при первой загрузке).",
                _ => "Необязательно: для закрытого репозитория Git покажет окно входа."
            };
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

        #region How to get a token

        private void BtnHowToken_Click(object sender, RoutedEventArgs e)
        {
            PanelHowToken.Visibility = PanelHowToken.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnOpenTokenPage_Click(object sender, RoutedEventArgs e)
        {
            TokenHelp.Open(this, (Button)sender);
        }

        #endregion

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            SourceEntry entry = new SourceEntry();
            _entries.Add(entry);
            EntriesList.SelectedItem = entry;
            _dirty = true;
            TxtUrl.Focus();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                return;
            }

            if (MessageBox.Show(this, $"Убрать «{_current.Title}» из списка?\n\nУже установленное в копиях не удаляется — для этого есть кнопка «Удалить из копии» в окне выбора.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            int i = _entries.IndexOf(_current);
            _entries.Remove(_current);
            _dirty = true;
            EntriesList.SelectedIndex = Math.Min(i, _entries.Count - 1);

            if (_entries.Count == 0)
            {
                ShowEntry(null);
            }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || string.IsNullOrWhiteSpace(_current.Url))
            {
                return;
            }

            SourceEntry entry = _current;
            BtnTest.IsEnabled = false;
            SetTest("проверяю…", "FgDim");

            try
            {
                List<RepoInfo> repos = await RepoCatalog.ListAsync(entry, CancellationToken.None);

                if (entry.IsAccount)
                {
                    int priv = repos.Count(r => r.Private);
                    SetTest($"найдено репозиториев: {repos.Count}{(priv > 0 ? $" (закрытых {priv})" : "")}", "Good");
                }
                else
                {
                    string sha = await GitRunner.RemoteHeadAsync(repos[0], CancellationToken.None);
                    SetTest("доступ есть, последний коммит " + UpdateEngine.Short(sha), "Good");
                }
            }
            catch (Exception ex)
            {
                SetTest(ex.Message, "Bad");
            }

            BtnTest.IsEnabled = true;
        }

        private void SetTest(string text, string brush)
        {
            TxtTest.Text = text;
            TxtTest.Foreground = (Brush)FindResource(brush);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string blockError = BlockGitHub.Validate() ?? BlockGitFlic.Validate();

            if (blockError != null)
            {
                MessageBox.Show(this, blockError, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SourceEntry bad = _entries.FirstOrDefault(x => SourceEntry.Parse(x.Url).Owner == null);

            if (bad != null)
            {
                EntriesList.SelectedItem = bad;
                MessageBox.Show(this, "Укажите адрес аккаунта или репозитория (или удалите пустую строку).", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<SourceEntry> all = new List<SourceEntry>();

            if (!BlockGitHub.IsEmpty)
            {
                all.Add(BlockGitHub.Entry);
            }

            if (!BlockGitFlic.IsEmpty)
            {
                all.Add(BlockGitFlic.Entry);
            }

            all.AddRange(_entries);
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
