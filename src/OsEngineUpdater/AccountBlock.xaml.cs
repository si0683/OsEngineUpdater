using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Аккаунт GitHub или GitFlic в «Мои аккаунты и репозитории»: адрес (можно просто логин), токен, проверка.
    /// Хранится обычной записью SourceEntry, как и раньше.
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

        /// <summary>Запись аккаунта (адрес уже приведён к полному виду).</summary>
        public SourceEntry Entry => _entry;

        public bool IsEmpty => string.IsNullOrWhiteSpace(TxtAccount.Text);

        private string ProviderName => _provider == GitProvider.GitFlic ? "GitFlic" : "GitHub";

        public void Load(GitProvider provider, SourceEntry entry)
        {
            _provider = provider;
            _entry = entry ?? new SourceEntry();
            _loading = true;

            TxtHeader.Text = ProviderName;
            TxtAccount.Text = _entry.Url ?? "";
            PwdToken.Password = _entry.Token ?? "";
            BtnHowToken.Content = TokenHelp.LinkText(provider);
            TokenHelp.Fill(TxtHowToken, BtnOpenTokenPage, BtnOpenTokenPage2, provider);
            TxtTokenHint.Text = provider == GitProvider.GitFlic
                ? "Обязателен: без токена GitFlic не отдаёт список проектов."
                : "Необязательно: нужен, чтобы видеть и ставить закрытые репозитории.";

            _loading = false;
            UpdateDetected();
        }

        /// <summary>null — всё в порядке (или блок пуст), иначе — что не так.</summary>
        public string Validate()
        {
            if (IsEmpty)
            {
                return null;
            }

            (GitProvider p, string owner, string repo) = SourceEntry.Parse(_entry.Url);

            if (p != _provider || owner == null || repo != null)
            {
                return $"{ProviderName}: укажите логин или адрес аккаунта, например {Example}. "
                       + "Отдельный репозиторий добавьте в список «Отдельные репозитории».";
            }

            return null;
        }

        private string Example => _provider == GitProvider.GitFlic ? "https://gitflic.ru/user/si0683" : "https://github.com/si0683";

        /// <summary>Логин → полный адрес аккаунта; адрес без https:// — дополняем.</summary>
        private string Normalize(string text)
        {
            string t = (text ?? "").Trim().TrimEnd('/');

            if (t.Length == 0)
            {
                return "";
            }

            if (!t.Contains('/') && !t.Contains('.'))
            {
                return _provider == GitProvider.GitFlic ? "https://gitflic.ru/user/" + t : "https://github.com/" + t;
            }

            if (!t.Contains("://"))
            {
                t = "https://" + t;
            }

            return t;
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
                TxtDetected.Text = $"Логин или адрес, например {Example}. Пусто — {ProviderName} не используется.";
                TxtDetected.Foreground = (Brush)FindResource("FgDim");
                return;
            }

            string error = Validate();

            if (error != null)
            {
                TxtDetected.Text = $"Здесь нужен аккаунт {ProviderName}, например {Example}.";
                TxtDetected.Foreground = (Brush)FindResource("Bad");
                BtnTest.IsEnabled = false;
                return;
            }

            TxtDetected.Text = _provider == GitProvider.GitFlic
                ? $"Аккаунт {_entry.Owner}: в окне выбора будут все его проекты"
                : $"Аккаунт {_entry.Owner}: в окне выбора будут все его репозитории";
            TxtDetected.Foreground = (Brush)FindResource("Accent");
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            SourceEntry entry = _entry;
            BtnTest.IsEnabled = false;
            SetTest("проверяю…", "FgDim");

            try
            {
                List<RepoInfo> repos = await RepoCatalog.ListAsync(entry, CancellationToken.None);
                int priv = repos.Count(r => r.Private);
                string what = _provider == GitProvider.GitFlic ? "проектов" : "репозиториев";
                SetTest($"найдено {what}: {repos.Count}{(priv > 0 ? $" (закрытых {priv})" : "")}", "Good");
            }
            catch (Exception ex)
            {
                SetTest(ex.Message, "Bad");
            }

            BtnTest.IsEnabled = !IsEmpty && Validate() == null;
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
