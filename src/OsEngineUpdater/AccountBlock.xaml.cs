using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// GitHub или GitFlic в записи «Мои аккаунты и репозитории»: аккаунт (можно просто логин)
    /// или адрес одного репозитория, токен, проверка. Хранится обычной SourceEntry.
    /// </summary>
    public partial class AccountBlock : UserControl
    {
        private GitProvider _provider = GitProvider.GitHub;
        private SourceEntry _entry = new SourceEntry();
        private bool _loading;

        /// <summary>Пользователь что-то изменил.</summary>
        public event EventHandler Changed;

        public AccountBlock()
        {
            InitializeComponent();
        }

        /// <summary>Запись (адрес уже приведён к полному виду).</summary>
        public SourceEntry Entry => _entry;

        public bool IsEmpty => string.IsNullOrWhiteSpace(TxtAccount.Text);

        private static string NameOf(GitProvider p) => p == GitProvider.GitFlic ? "GitFlic" : "GitHub";

        private static string ExampleOf(GitProvider p) =>
            p == GitProvider.GitFlic ? "https://gitflic.ru/user/si0683" : "https://github.com/si0683";

        public void Load(GitProvider provider, SourceEntry entry)
        {
            _provider = provider;
            _entry = entry ?? new SourceEntry();
            _loading = true;

            TxtHeader.Text = NameOf(provider);
            TxtAccount.Text = _entry.Url ?? "";
            PwdToken.Password = _entry.Token ?? "";
            TxtTest.Text = "";
            PanelHowToken.Visibility = Visibility.Collapsed;
            BtnHowToken.Content = TokenHelp.LinkText(provider);
            TokenHelp.Fill(TxtHowToken, BtnOpenTokenPage, BtnOpenTokenPage2, provider);
            TxtTokenHint.Text = provider == GitProvider.GitFlic
                ? "Для аккаунта GitFlic обязателен: без токена GitFlic не отдаёт список проектов."
                : "Необязательно: нужен, чтобы видеть и ставить закрытые репозитории.";

            _loading = false;
            UpdateDetected();
        }

        /// <summary>null — всё в порядке (или адрес пуст), иначе — что не так.</summary>
        public static string Validate(GitProvider provider, SourceEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Url))
            {
                return null;
            }

            (GitProvider p, string owner, _) = SourceEntry.Parse(entry.Url);

            if (p != provider || owner == null)
            {
                return $"{NameOf(provider)}: укажите логин или адрес, например {ExampleOf(provider)}.";
            }

            return null;
        }

        /// <summary>
        /// Логин или «логин/репозиторий» → полный адрес; адрес без https:// — дополняем.
        /// </summary>
        private string Normalize(string text)
        {
            string t = (text ?? "").Trim().TrimEnd('/');

            if (t.Length == 0 || t.Contains("://"))
            {
                return t;
            }

            string[] seg = t.Split('/');

            if (!seg[0].Contains('.'))
            {
                // si0683 или si0683/ScriptsOS
                if (_provider == GitProvider.GitFlic)
                {
                    return seg.Length == 1 ? "https://gitflic.ru/user/" + seg[0] : "https://gitflic.ru/project/" + seg[0] + "/" + seg[1];
                }

                return "https://github.com/" + t;
            }

            return "https://" + t;
        }

        private void Field_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            _entry.Url = Normalize(TxtAccount.Text);

            if (sender == PwdToken)
            {
                _entry.Token = PwdToken.Password;
            }

            TxtTest.Text = "";
            UpdateDetected();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateDetected()
        {
            BtnTest.IsEnabled = !IsEmpty;

            if (IsEmpty)
            {
                TxtDetected.Text = $"Логин или адрес, например {ExampleOf(_provider)}. Пусто — {NameOf(_provider)} в этой записи не используется.";
                TxtDetected.Foreground = (Brush)FindResource("FgDim");
                return;
            }

            if (Validate(_provider, _entry) != null)
            {
                TxtDetected.Text = $"Не похоже на адрес {NameOf(_provider)}. Например: {ExampleOf(_provider)}";
                TxtDetected.Foreground = (Brush)FindResource("Bad");
                BtnTest.IsEnabled = false;
                return;
            }

            if (_entry.IsAccount)
            {
                TxtDetected.Text = _provider == GitProvider.GitFlic
                    ? $"Аккаунт {_entry.Owner}: в окне выбора будут все его проекты"
                    : $"Аккаунт {_entry.Owner}: в окне выбора будут все его репозитории";
            }
            else
            {
                TxtDetected.Text = $"Один репозиторий: {_entry.Owner}/{_entry.RepoName}";
            }

            TxtDetected.Foreground = (Brush)FindResource("Good");
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            SourceEntry entry = _entry;
            BtnTest.IsEnabled = false;
            SetTest("проверяю…", "FgDim");

            try
            {
                List<RepoInfo> repos = await RepoCatalog.ListAsync(entry, CancellationToken.None);

                if (entry.IsAccount)
                {
                    int priv = repos.Count(r => r.Private);
                    string what = _provider == GitProvider.GitFlic ? "проектов" : "репозиториев";
                    SetTest($"найдено {what}: {repos.Count}{(priv > 0 ? $" (закрытых {priv})" : "")}", "Good");
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

            BtnTest.IsEnabled = !IsEmpty && Validate(_provider, _entry) == null;
        }

        private void SetTest(string text, string brush)
        {
            TxtTest.Text = text;
            TxtTest.Foreground = (Brush)FindResource(brush);
        }

        private void BtnHowToken_Click(object sender, RoutedEventArgs e)
        {
            PanelHowToken.Visibility = PanelHowToken.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnOpenTokenPage_Click(object sender, RoutedEventArgs e)
        {
            TokenHelp.Open(Window.GetWindow(this), (Button)sender);
        }
    }
}
