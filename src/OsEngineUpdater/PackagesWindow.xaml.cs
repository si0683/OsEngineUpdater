using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Выбор своих роботов / индикаторов / файлов и установка их в копию.
    /// Changed = true — что-то установлено или удалено.
    /// </summary>
    public partial class PackagesWindow : Window
    {
        private readonly CopyInfo _copy;
        private readonly UpdaterConfig _cfg;
        private readonly PackageKind _kind;
        private UserSettings _settings;
        private readonly ObservableCollection<PackageRow> _rows = new ObservableCollection<PackageRow>();
        private CancellationTokenSource _cts;

        public bool Changed { get; private set; }

        public PackagesWindow(CopyInfo copy, UpdaterConfig cfg, PackageKind kind, UserSettings settings)
        {
            InitializeComponent();
            _copy = copy;
            _cfg = cfg;
            _kind = kind;
            _settings = settings;
            RowsList.ItemsSource = _rows;

            string what = kind switch
            {
                PackageKind.Robot => "роботы",
                PackageKind.Indicator => "индикаторы",
                _ => "свои файлы"
            };

            Title = "Мои " + what;
            TxtTitle.Text = $"Мои {what} → {copy.Name}";
            TxtSubtitle.Text = kind switch
            {
                PackageKind.Robot => "Папка роботов копии: " + cfg.EffectiveRobotsDir,
                PackageKind.Indicator => "Папка индикаторов копии: " + cfg.EffectiveIndicatorsDir,
                _ => "Файлы раскладываются в папки, указанные в «Мои репозитории», и не затираются обновлением OsEngine"
            };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            _rows.Clear();
            List<InstalledPackage> installed = PackageManager.LoadInstalled(_copy);

            foreach (PackageSource src in _settings.Sources.Where(s => s.Kind == _kind))
            {
                PackageRow row = new PackageRow { Source = src, Installed = installed.FirstOrDefault(p => p.SourceId == src.Id) };
                row.Target = "→ " + SafeTarget(src);
                _rows.Add(row);
            }

            // установлено раньше, но репозиторий убран из списка — даём удалить
            foreach (InstalledPackage p in installed.Where(p => p.Kind == _kind && _rows.All(r => r.Installed != p)))
            {
                _rows.Add(new PackageRow { Installed = p, Target = "→ " + p.TargetDir });
            }

            foreach (PackageRow r in _rows)
            {
                r.SetLocalStatus(this);
            }

            TxtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Text = "Список пуст.\nДобавьте свои репозитории кнопкой «Мои репозитории…».";
            UpdateButtons();

            // проверка обновлений на сервере (ls-remote), по одному
            foreach (PackageRow r in _rows.Where(r => r.Source != null).ToList())
            {
                try
                {
                    string head = await GitRunner.RemoteHeadAsync(r.Source, CancellationToken.None);
                    r.RemoteHead = head;
                    r.SetLocalStatus(this);
                }
                catch (Exception ex)
                {
                    r.SetStatus("нет доступа: " + ex.Message, Brush("Bad"));
                }
            }
        }

        private string SafeTarget(PackageSource src)
        {
            try
            {
                string rel = Path.GetRelativePath(_copy.Root, PackageManager.TargetDir(_copy, _cfg, src));
                return rel == "." ? "корень копии" : rel;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            List<PackageRow> rows = _rows.Where(r => r.IsChecked && r.Source != null).ToList();

            if (rows.Count == 0)
            {
                AppendLog(LogLevel.Warn, "Отметьте галочками, что установить.");
                return;
            }

            await RunAsync(async (log, ct) =>
            {
                int ok = 0;

                foreach (PackageRow r in rows)
                {
                    PackageResult res = await PackageManager.InstallAsync(_copy, _cfg, r.Source, log,
                        msg => Dispatcher.Invoke(() => Confirm(msg)), ct);

                    if (res.Done)
                    {
                        ok++;
                    }
                }

                if (ok > 0)
                {
                    Changed = true;
                    VsRefresher.Run(_copy, log, false);
                }
            });
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            List<PackageRow> rows = _rows.Where(r => r.IsChecked && r.Installed != null).ToList();

            if (rows.Count == 0)
            {
                AppendLog(LogLevel.Warn, "Отметьте установленные, которые нужно удалить из копии.");
                return;
            }

            if (MessageBox.Show(this, "Удалить из копии:\n  " + string.Join("\n  ", rows.Select(r => r.Name)) + "\n\nРепозитории не затрагиваются.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            await RunAsync((log, ct) =>
            {
                foreach (PackageRow r in rows)
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
                await ReloadAsync();
            }
        }

        private async Task RunAsync(Func<UpdateLog, CancellationToken, Task> body)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            UpdateLog log = new UpdateLog(_copy.LogPath, (l, m) => Dispatcher.BeginInvoke(() => AppendLog(l, m)));

            try
            {
                CancellationToken ct = _cts.Token;
                await Task.Run(() => body(log, ct));
            }
            catch (OperationCanceledException)
            {
                AppendLog(LogLevel.Warn, "Отменено.");
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

            foreach (PackageRow r in _rows)
            {
                r.IsChecked = false;
            }

            await ReloadAsync();
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
            RowsList.IsEnabled = !busy;
            BtnClose.Content = busy ? "Отмена" : "Закрыть";
        }

        private void UpdateButtons()
        {
            bool any = _rows.Count > 0;
            BtnInstall.IsEnabled = any && GitRunner.Available;
            BtnUninstall.IsEnabled = _rows.Any(r => r.Installed != null);

            if (!GitRunner.Available)
            {
                AppendLog(LogLevel.Warn, "Git не найден. Установите Git for Windows (git-scm.com) и перезапустите программу.");
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                e.Cancel = true;
            }
        }

        private void AppendLog(LogLevel level, string message)
        {
            string prefix = level switch { LogLevel.Warn => "! ", LogLevel.Error => "✖ ", LogLevel.Ok => "✔ ", _ => "" };
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}{Environment.NewLine}");
            TxtLog.ScrollToEnd();
        }

        internal Brush Brush(string key) => (Brush)FindResource(key);
    }

    /// <summary>Строка списка: репозиторий из настроек и/или установленный пакет.</summary>
    public sealed class PackageRow : INotifyPropertyChanged
    {
        public PackageSource Source { get; init; }
        public InstalledPackage Installed { get; init; }
        public string RemoteHead { get; set; }

        public string Name => Source?.DisplayName ?? Installed?.Name;
        public string Url => Source?.Url ?? (Installed?.Url + "  (убран из «Мои репозитории»)");
        public string Target { get; set; }

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

        public void SetStatus(string text, Brush brush)
        {
            Status = text;
            StatusBrush = brush;
        }

        internal void SetLocalStatus(PackagesWindow w)
        {
            if (Installed == null)
            {
                SetStatus("не установлен", w.Brush("FgDim"));
                return;
            }

            string when = DateTime.TryParse(Installed.InstalledUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt)
                ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "";

            if (RemoteHead != null && !string.Equals(RemoteHead, Installed.Commit, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"есть обновление ({UpdateEngine.Short(Installed.Commit)} → {UpdateEngine.Short(RemoteHead)})", w.Brush("Warn"));
            }
            else
            {
                SetStatus($"установлен {UpdateEngine.Short(Installed.Commit)}\n{when}", w.Brush("Good"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
