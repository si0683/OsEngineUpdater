using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>
    /// Инструкция «Где взять токен» для GitHub и GitFlic: текст с **жирным** и кнопки, открывающие страницу создания токена.
    /// </summary>
    public static class TokenHelp
    {
        private const string GitHubFineGrainedUrl = "https://github.com/settings/personal-access-tokens/new";
        private const string GitHubClassicUrl = "https://github.com/settings/tokens/new?description=OsEngineUpdater&scopes=repo";
        private const string GitFlicSettingsUrl = "https://gitflic.ru/settings";

        public static string LinkText(GitProvider provider) =>
            provider == GitProvider.GitFlic ? "Как получить токен GitFlic?" : "Как получить токен GitHub?";

        /// <summary>Заполнить текст инструкции и кнопки (вторая — только для GitHub).</summary>
        public static void Fill(TextBlock target, Button open, Button open2, GitProvider provider)
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
                SetButton(open, "Открыть настройки GitFlic", GitFlicSettingsUrl);
                open2.Visibility = Visibility.Collapsed;
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
                SetButton(open, "Создать токен на GitHub", GitHubFineGrainedUrl);
                SetButton(open2, "Классический токен (repo)", GitHubClassicUrl);
                open2.Visibility = Visibility.Visible;
            }

            target.Inlines.Clear();
            string[] parts = text.Split("**");

            for (int i = 0; i < parts.Length; i++)
            {
                string[] lines = parts[i].Split('\n');

                for (int j = 0; j < lines.Length; j++)
                {
                    if (j > 0)
                    {
                        target.Inlines.Add(new LineBreak());
                    }

                    Run run = new Run(lines[j]);

                    if (i % 2 == 1)
                    {
                        run.FontWeight = FontWeights.SemiBold;
                    }

                    target.Inlines.Add(run);
                }
            }
        }

        /// <summary>Открыть в браузере страницу из Tag кнопки.</summary>
        public static void Open(Window owner, Button b)
        {
            try
            {
                Process.Start(new ProcessStartInfo((string)b.Tag) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Не удалось открыть браузер: " + ex.Message, owner?.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void SetButton(Button b, string text, string url)
        {
            b.Content = text;
            b.Tag = url;
            b.ToolTip = url;
        }
    }
}
