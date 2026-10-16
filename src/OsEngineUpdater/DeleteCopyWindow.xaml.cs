using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Подтверждение удаления копии: размер, блокирующие причины, ввод имени папки.
    /// DialogResult = true — копия удалена.
    /// </summary>
    public partial class DeleteCopyWindow : Window
    {
        private readonly CopyInfo _copy;
        private readonly List<CopyInfo> _others;
        private readonly CancellationTokenSource _sizeCts = new CancellationTokenSource();
        private readonly string _confirmName;
        private bool _blocked = true;

        public DeleteCopyWindow(CopyInfo copy, IEnumerable<CopyInfo> others)
        {
            InitializeComponent();
            _copy = copy;
            _others = others.ToList();
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
                long size = await Task.Run(() => CopyRemover.GetSize(_copy.Root, _sizeCts.Token));
                TxtSize.Text = "размер: " + CopyRemover.FormatSize(size);
            }
            catch (OperationCanceledException)
            {
                // окно закрыли
            }
            catch (Exception ex)
            {
                TxtSize.Text = "размер: не удалось посчитать (" + ex.Message + ")";
            }
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
            BtnDelete.IsEnabled = !_blocked
                                  && string.Equals(TxtConfirm.Text.Trim(), _confirmName, StringComparison.OrdinalIgnoreCase);
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            // перед удалением проверяем ещё раз — за это время могли запустить OsEngine
            await CheckBlockersAsync();

            if (_blocked)
            {
                return;
            }

            _sizeCts.Cancel();
            IsEnabled = false;

            try
            {
                bool deleted = CopyRemover.Delete(_copy, true);

                if (!deleted)
                {
                    MessageBox.Show(this, "Удаление отменено или выполнено не полностью — папка ещё существует.\nАвтообновление этой копии выключено.",
                        "Удалить копию", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                DialogResult = deleted;
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                MessageBox.Show(this, "Не удалось удалить: " + ex.Message, "Удалить копию", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _sizeCts.Cancel();
        }
    }
}
