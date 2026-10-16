using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
                BtnHowToken.Content = "Как получить токен?";
                FillHowToken(GitProvider.GitHub);
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
            BtnHowToken.Content = howFor == GitProvider.GitFlic ? "Как получить токен GitFlic?" : "Как получить токен GitHub?";
            FillHowToken(howFor);

            TxtTokenHint.Text = _current.Provider switch
            {
                GitProvider.GitHub => "Необязательно: нужен, чтобы видеть и ставить закрытые репозитории.",
                GitProvider.GitFlic when account => "Обязателен: без токена GitFlic не отдаёт список проектов.",
                GitProvider.GitFlic => "Нужен для закрытого проекта (или войдёте через окно Git при первой загрузке).",
                _ => "Необязательно: для закрытого репозитория Git покажет окно входа."
            };
        }

        #region How to get a token

        private const string GitHubFineGrainedUrl = "https://github.com/settings/personal-access-tokens/new";
        private const string GitHubClassicUrl = "https://github.com/settings/tokens/new?description=OsEngineUpdater&scopes=repo";
        private const string GitFlicSettingsUrl = "https://gitflic.ru/settings";

        private void BtnHowToken_Click(object sender, RoutedEventArgs e)
        {
            PanelHowToken.Visibility = PanelHowToken.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Короткая инструкция: где взять токен (текст с **жирным**).</summary>
        private void FillHowToken(GitProvider provider)
        {
            string text;

            if (provider == GitProvider.GitFlic)
            {
                text = "**Где взять токен GitFlic**\n"
                       + "1. Нажмите «Открыть настройки GitFlic» и войдите в свой аккаунт.\n"
                       + "2. В профиле откройте раздел **«API токены»** → **«Создать»**.\n"
                       + "3. Название — например **OsEngineUpdater**, срок действия — по желанию.\n"
                       + "4. Права: **PROJECT_READ** (список проектов) и **REPOSITORY_READ** (скачивание файлов). Других прав не нужно.\n"
                       + "5. Нажмите «Создать», скопируйте токен и вставьте в поле «Токен» выше → «Проверить» → «Сохранить».\n"
                       + "Токен показывается один раз — если потеряли, создайте новый.";
                SetTokenButton(BtnOpenTokenPage, "Открыть настройки GitFlic", GitFlicSettingsUrl);
                BtnOpenTokenPage2.Visibility = Visibility.Collapsed;
            }
            else
            {
                text = "**Где взять токен GitHub**\n"
                       + "1. Нажмите «Создать токен на GitHub» и войдите в свой аккаунт.\n"
                       + "2. Token name — например **OsEngineUpdater**, Expiration — 90 дней или год.\n"
                       + "3. Repository access — **All repositories** (или Only select repositories — тогда утилита увидит только выбранные).\n"
                       + "4. Permissions → Repository permissions → **Contents: Read-only** (Metadata: Read-only выставится сам). Остальное не трогайте.\n"
                       + "5. Generate token → скопируйте токен (начинается с github_pat_) и вставьте в поле «Токен» выше → «Проверить» → «Сохранить».\n"
                       + "Такой токен может только читать. Классический токен тоже подойдёт — нужна одна галочка **repo**, но он даёт и право записи.";
                SetTokenButton(BtnOpenTokenPage, "Создать токен на GitHub", GitHubFineGrainedUrl);
                SetTokenButton(BtnOpenTokenPage2, "Классический токен (repo)", GitHubClassicUrl);
                BtnOpenTokenPage2.Visibility = Visibility.Visible;
            }

            TxtHowToken.Inlines.Clear();
            string[] parts = text.Split("**");

            for (int i = 0; i < parts.Length; i++)
            {
                string[] lines = parts[i].Split('\n');

                for (int j = 0; j < lines.Length; j++)
                {
                    if (j > 0)
                    {
                        TxtHowToken.Inlines.Add(new LineBreak());
                    }

                    Run run = new Run(lines[j]);

                    if (i % 2 == 1)
                    {
                        run.FontWeight = FontWeights.SemiBold;
                    }

                    TxtHowToken.Inlines.Add(run);
                }
            }
        }

        private static void SetTokenButton(Button b, string text, string url)
        {
            b.Content = text;
            b.Tag = url;
            b.ToolTip = url;
        }

        private void BtnOpenTokenPage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo((string)((Button)sender).Tag) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть браузер: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
