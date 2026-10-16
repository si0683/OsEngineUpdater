using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Общий список своих репозиториев (роботы, индикаторы, свои файлы) для всех копий.
    /// DialogResult = true — настройки сохранены.
    /// </summary>
    public partial class ReposWindow : Window
    {
        private readonly UserSettings _settings;
        private readonly ObservableCollection<PackageSource> _sources;
        private PackageSource _current;
        private bool _loading;
        private bool _dirty;

        public ReposWindow(UserSettings settings)
        {
            InitializeComponent();
            _settings = settings.Clone();
            _sources = new ObservableCollection<PackageSource>(_settings.Sources);
            SourcesList.ItemsSource = _sources;

            TxtGit.Text = GitRunner.Available ? "Git: " + GitRunner.GitPath : "Git не найден — установите Git for Windows (git-scm.com)";

            if (_sources.Count > 0)
            {
                SourcesList.SelectedIndex = 0;
            }
            else
            {
                ShowSource(null);
            }
        }

        private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowSource(SourcesList.SelectedItem as PackageSource);
        }

        private void ShowSource(PackageSource src)
        {
            _current = src;
            _loading = true;

            FormGrid.IsEnabled = src != null;
            BtnRemove.IsEnabled = src != null;
            TxtTest.Text = "";

            foreach (ComboBoxItem it in CmbKind.Items)
            {
                if ((string)it.Tag == (src?.Kind ?? PackageKind.Robot).ToString())
                {
                    CmbKind.SelectedItem = it;
                }
            }

            TxtUrl.Text = src?.Url ?? "";
            TxtName.Text = src?.Name ?? "";
            TxtBranch.Text = src?.Branch ?? "";
            TxtSubPath.Text = src?.SubPath ?? "";
            TxtTarget.Text = src?.TargetPath ?? "";

            _loading = false;
            UpdateHints();
        }

        private void Form_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _current == null)
            {
                return;
            }

            _current.Kind = Enum.Parse<PackageKind>((string)((ComboBoxItem)CmbKind.SelectedItem).Tag);
            _current.Url = TxtUrl.Text.Trim();
            _current.Name = TxtName.Text.Trim();
            _current.Branch = TxtBranch.Text.Trim();
            _current.SubPath = TxtSubPath.Text.Trim();
            _current.TargetPath = TxtTarget.Text.Trim();
            _dirty = true;

            SourcesList.Items.Refresh();
            UpdateHints();
        }

        private void UpdateHints()
        {
            bool files = _current?.Kind == PackageKind.Files;
            LblTarget.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            TxtTarget.Visibility = files ? Visibility.Visible : Visibility.Collapsed;

            if (_current == null)
            {
                TxtNameHint.Text = "";
                return;
            }

            string folder = PackageManager.SafeName(_current.DisplayName);

            TxtNameHint.Text = _current.Kind switch
            {
                PackageKind.Robot => $"Ставится в <папка роботов копии>\\{folder}",
                PackageKind.Indicator => $"Ставится в <папка индикаторов копии>\\{folder}",
                _ => "Только для списка. Файлы раскладываются в «Куда в копии» с сохранением структуры репозитория и не затираются обновлением OsEngine."
            };
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            PackageSource src = new PackageSource();
            _sources.Add(src);
            SourcesList.SelectedItem = src;
            _dirty = true;
            TxtUrl.Focus();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                return;
            }

            if (MessageBox.Show(this, $"Убрать «{_current.DisplayName}» из списка?\n\nУже установленные в копии файлы не удаляются — для этого есть кнопка «Удалить» в окне выбора.",
                    "Мои репозитории", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            int i = _sources.IndexOf(_current);
            _sources.Remove(_current);
            _dirty = true;
            SourcesList.SelectedIndex = Math.Min(i, _sources.Count - 1);

            if (_sources.Count == 0)
            {
                ShowSource(null);
            }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || string.IsNullOrWhiteSpace(_current.Url))
            {
                return;
            }

            PackageSource src = _current;
            BtnTest.IsEnabled = false;
            TxtTest.Foreground = (System.Windows.Media.Brush)FindResource("FgDim");
            TxtTest.Text = "проверяю… (для закрытого репозитория может открыться окно входа)";

            try
            {
                string sha = await GitRunner.RemoteHeadAsync(src, CancellationToken.None);
                TxtTest.Foreground = (System.Windows.Media.Brush)FindResource("Good");
                TxtTest.Text = "доступ есть, последний коммит " + UpdateEngine.Short(sha);
            }
            catch (Exception ex)
            {
                TxtTest.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
                TxtTest.Text = ex.Message;
            }

            BtnTest.IsEnabled = true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            PackageSource empty = _sources.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Url));

            if (empty != null)
            {
                SourcesList.SelectedItem = empty;
                MessageBox.Show(this, "Укажите адрес репозитория (или удалите пустую строку).", "Мои репозитории",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dup = _sources
                .GroupBy(s => (s.Kind, Folder: PackageManager.SafeName(s.DisplayName).ToLowerInvariant()))
                .FirstOrDefault(g => g.Key.Kind != PackageKind.Files && g.Count() > 1);

            if (dup != null)
            {
                MessageBox.Show(this, $"Два репозитория типа «{PackageSource.KindToText(dup.Key.Kind)}» ставятся в одну папку «{dup.Key.Folder}». Задайте разные названия.",
                    "Мои репозитории", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.Sources = _sources.ToList();

            try
            {
                _settings.Save();
                _dirty = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "Мои репозитории", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_dirty && DialogResult != true
                && MessageBox.Show(this, "Закрыть без сохранения?", "Мои репозитории", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        }
    }
}
