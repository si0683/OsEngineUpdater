using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// «Мои аккаунты и репозитории» — общий список для всех копий: аккаунты GitHub / GitFlic и отдельные репозитории.
    /// DialogResult = true — настройки сохранены.
    /// </summary>
    public partial class ReposWindow : Window
    {
        private readonly UserSettings _settings;
        private readonly ObservableCollection<SourceEntry> _entries;
        private SourceEntry _current;
        private bool _loading;
        private bool _dirty;

        public ReposWindow(UserSettings settings)
        {
            InitializeComponent();
            _settings = settings.Clone();
            _entries = new ObservableCollection<SourceEntry>(_settings.Entries);
            EntriesList.ItemsSource = _entries;

            TxtGit.Text = GitRunner.Available ? "Git: " + GitRunner.GitPath : "Git не найден — установите Git for Windows (git-scm.com)";

            if (_entries.Count > 0)
            {
                EntriesList.SelectedIndex = 0;
            }
            else
            {
                ShowEntry(null);
            }
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

            TxtTokenHint.Text = _current.Provider switch
            {
                GitProvider.GitHub => "Необязательно. Нужен, чтобы видеть и ставить закрытые репозитории. GitHub → Settings → Developer settings → Personal access tokens (доступ на чтение репозиториев).",
                GitProvider.GitFlic when account => "Обязателен: без токена GitFlic не отдаёт список проектов. GitFlic → профиль → «API токены» → «Создать».",
                GitProvider.GitFlic => "Нужен для закрытого проекта (или войдёте через окно Git при первой загрузке).",
                _ => "Необязательно: для закрытого репозитория Git покажет окно входа."
            };
        }

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
            SourceEntry bad = _entries.FirstOrDefault(x => SourceEntry.Parse(x.Url).Owner == null);

            if (bad != null)
            {
                EntriesList.SelectedItem = bad;
                MessageBox.Show(this, "Укажите адрес аккаунта или репозитория (или удалите пустую строку).", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.Entries = _entries.ToList();

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
