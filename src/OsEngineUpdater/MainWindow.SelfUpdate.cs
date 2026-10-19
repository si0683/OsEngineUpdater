using System.Windows;
using System.Windows.Threading;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Обновление самой программы, пока окно открыто: проверка релиза с заданной частотой,
    /// установка новой версии и перезапуск. Не прерывает работу: если идёт операция
    /// или есть несохранённые настройки, установка откладывается до следующей минуты.
    /// </summary>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _selfTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        private ReleaseInfo _pendingSelfUpdate;
        private bool _selfBusy;
        private bool _restarting;
        private bool _postponeLogged;
        private string _selfStatus = "";

        private void InitSelfUpdate()
        {
            if (Program.AfterUpdate)
            {
                AppendLog(LogLevel.Ok, $"OsEngine Updater обновлён до версии {SelfUpdater.CurrentVersionText}.");
            }

            UpdateAppVersionText();
            _selfTimer.Tick += async (_, _) => await SelfTickAsync();
            _selfTimer.Start();

            // первая проверка — вскоре после запуска, чтобы не мешать загрузке копий
            DispatcherTimer first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            first.Tick += async (_, _) =>
            {
                first.Stop();
                await SelfTickAsync();
            };
            first.Start();
        }

        private async Task SelfTickAsync()
        {
            if (_selfBusy || _restarting)
            {
                return;
            }

            _selfBusy = true;

            try
            {
                UserSettings s = UserSettings.Load();

                if (_pendingSelfUpdate == null && SelfUpdater.IsDue(s))
                {
                    SelfUpdater.MarkChecked();
                    ReleaseInfo r = await SelfUpdater.GetLatestAsync(s.SelfUpdateRepo, CancellationToken.None);

                    if (SelfUpdater.IsNewer(r))
                    {
                        (bool can, string why) = SelfUpdater.CanSelfUpdate();

                        if (can)
                        {
                            _pendingSelfUpdate = r;
                            AppendLog(LogLevel.Info, $"Вышла новая версия OsEngine Updater {SelfUpdater.Short(r.Version)} — устанавливаю.");
                        }
                        else
                        {
                            _selfStatus = $"вышла {SelfUpdater.Short(r.Version)}";
                            AppendLog(LogLevel.Warn, $"Вышла новая версия OsEngine Updater {SelfUpdater.Short(r.Version)}, но обновиться само не может: {why}.");
                        }
                    }
                    else
                    {
                        _selfStatus = r == null ? "" : "последняя версия";
                    }

                    UpdateAppVersionText();
                }

                if (_pendingSelfUpdate != null)
                {
                    if (IsBusy || _dirty)
                    {
                        if (!_postponeLogged)
                        {
                            AppendLog(LogLevel.Info, "Обновление программы отложено: дождусь окончания операции и сохранения настроек.");
                            _postponeLogged = true;
                        }

                        return;
                    }

                    await ApplySelfUpdateAsync(_pendingSelfUpdate);
                }
            }
            catch (Exception ex)
            {
                _selfStatus = "проверка не удалась";
                UpdateAppVersionText();
                AppendLog(LogLevel.Warn, "Проверка новой версии программы не удалась: " + ex.Message);
            }
            finally
            {
                _selfBusy = false;
            }
        }

        private async Task ApplySelfUpdateAsync(ReleaseInfo r)
        {
            try
            {
                string exe = await Task.Run(() => SelfUpdater.DownloadAndReplaceAsync(r,
                    msg => Dispatcher.BeginInvoke(() => AppendLog(LogLevel.Info, msg)), CancellationToken.None));

                _restarting = true;
                AppendLog(LogLevel.Ok, "Перезапускаю программу…");
                SelfUpdater.Restart(exe);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _pendingSelfUpdate = null;
                _selfStatus = "обновление не удалось";
                UpdateAppVersionText();
                AppendLog(LogLevel.Error, "Не удалось обновить программу: " + ex.Message);
            }
        }

        private void UpdateAppVersionText()
        {
            string tail = _selfStatus.Length > 0 ? " · " + _selfStatus : "";
            TxtAppVersion.Text = $"OsEngine Updater {SelfUpdater.CurrentVersionText}{tail}";
            TxtAppVersion.ToolTip = "Обновление программы настраивается в «Мои аккаунты и репозитории…»";
        }
    }
}
