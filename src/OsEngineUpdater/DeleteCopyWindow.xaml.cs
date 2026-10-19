using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Подтверждение удаления копии: размер, что будет закрыто, ввод имени папки.
    /// После подтверждения программа сама закрывает всё, что держит файлы копии (Visual Studio, OsEngine,
    /// сборку, git), и отправляет папку в Корзину. DialogResult = true — копия удалена.
    /// </summary>
    public partial class DeleteCopyWindow : Window
    {
        private readonly CopyInfo _copy;
        private readonly List<CopyInfo> _others;
        private readonly Action _releaseOwnHandles;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly string _confirmName;
        private bool _blocked = true;
        private bool _deleting;

        /// <param name="releaseOwnHandles">Отпустить файлы копии, которые держит сама программа (слежение за папкой).</param>
        public DeleteCopyWindow(CopyInfo copy, IEnumerable<CopyInfo> others, Action releaseOwnHandles)
        {
            InitializeComponent();
            _copy = copy;
            _others = others.ToList();
            _releaseOwnHandles = releaseOwnHandles;
            _confirmName = Path.GetFileName(copy.Root);

            TxtTitle.Text = $"Удалить копию «{copy.Name}» с диска?";
            TxtPath.Text = copy.Root;
            TxtSize.Text = "размер: считаю…";
            TxtConfirmHint.Text = $"Для подтверждения введите имя папки: {_confirmName}";
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TxtConfirm.Focus();
            await CheckBlockersAsync();

            try
            {
                long size = await Task.Run(() => CopyRemover.GetSize(_copy.Root, _cts.Token));
                TxtSize.Text = "размер: " + CopyRemover.FormatSize(size);

                List<LockingProcess> lockers = await Task.Run(() => FileLockers.Find(_copy.Root, _cts.Token));
                ShowLockers(lockers);
            }
            catch (OperationCanceledException)
            {
                // окно закрыли
            }
            catch (Exception ex)
            {
                TxtLockersInfo.Text = "не удалось проверить: " + ex.Message;
            }
        }

        private void ShowLockers(List<LockingProcess> lockers)
        {
            List<string> closable = lockers.Where(FileLockers.CanClose).Select(x => x.Title).ToList();
            List<string> system = lockers.Where(x => !FileLockers.CanClose(x)).Select(x => x.Title).ToList();

            ListLockers.ItemsSource = closable;
            PanelLockers.Visibility = closable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtLockersInfo.Text = system.Count > 0
                ? "Файлы держит системная программа: " + string.Join(", ", system) + " — её программа не закрывает. Если удаление не пройдёт, повторите через минуту."
                : (closable.Count == 0 ? "Файлы копии никакими программами не заняты." : "");
            TxtLockersInfo.Visibility = TxtLockersInfo.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task CheckBlockersAsync()
        {
            List<string> blockers = await Task.Run(() => CopyRemover.FindBlockers(_copy, _others));
            _blocked = blockers.Count > 0;
            ListBlockers.ItemsSource = blockers;
            PanelBlockers.Visibility = _blocked ? Visibility.Visible : Visibility.Collapsed;
            UpdateDeleteButton();
        }

        private async void BtnRecheck_Click(object sender, RoutedEventArgs e)
        {
            ((Button)sender).IsEnabled = false;
            await CheckBlockersAsync();
            ((Button)sender).IsEnabled = true;
        }

        private void TxtConfirm_TextChanged(object sender, TextChangedEventArgs e) => UpdateDeleteButton();

        private void UpdateDeleteButton()
        {
            BtnDelete.IsEnabled = !_blocked && !_deleting
                                  && string.Equals(TxtConfirm.Text.Trim(), _confirmName, StringComparison.OrdinalIgnoreCase);
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            await CheckBlockersAsync();

            if (_blocked)
            {
                return;
            }

            _cts.Cancel();
            _deleting = true;
            UpdateDeleteButton();
            TxtConfirm.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            TxtProgress.Visibility = Visibility.Visible;
            SetProgress("Закрываю программы, которые держат файлы копии, и удаляю папку…");

            // сначала отпускаем свои файлы копии (слежение за папкой в главном окне)
            _releaseOwnHandles?.Invoke();

            string error = await Task.Run(() => CopyRemover.Delete(_copy, msg => Dispatcher.BeginInvoke(() => SetProgress(msg))));
            _deleting = false; // до DialogResult: иначе Window_Closing отменит закрытие

            if (error == null)
            {
                DialogResult = true;
                return;
            }

            BtnCancel.IsEnabled = true;
            TxtConfirm.IsEnabled = true;
            UpdateDeleteButton();
            SetProgress("Не удалось удалить: " + error);
            TxtProgress.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
        }

        private void SetProgress(string text)
        {
            TxtProgress.Text = text;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_deleting)
            {
                e.Cancel = true; // дождаться окончания удаления
                return;
            }

            _cts.Cancel();
        }
    }
}
