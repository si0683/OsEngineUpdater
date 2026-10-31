using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    /// <summary>Заставка при запуске: пока ищутся копии, потом плавно уступает место главному окну.</summary>
    public partial class SplashWindow : Window
    {
        /// <summary>Чтобы заставка не мелькала, она видна хотя бы столько.</summary>
        private const int MinShowMs = 1100;

        public SplashWindow()
        {
            InitializeComponent();

            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = v == null ? "" : $"версия {v.Major}.{v.Minor}.{v.Build}";

            if (Program.AfterUpdate)
            {
                TxtStatus.Text = "Программа обновлена, запускаю…";
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CardClip.Rect = new Rect(0, 0, ActualWidth - 40, ActualHeight - 40);

            Animate(this, OpacityProperty, 0, 1, 260);
            Animate(Scale, ScaleTransform.ScaleXProperty, 0.96, 1, 320);
            Animate(Scale, ScaleTransform.ScaleYProperty, 0.96, 1, 320);

            RunnerShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-Runner.Width, Track.ActualWidth, TimeSpan.FromMilliseconds(1300))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });

            Task delay = Task.Delay(MinShowMs);
            List<string> roots;

            try
            {
                roots = await Task.Run(CopyRegistry.LoadWithDiscovery);
            }
            catch
            {
                roots = null; // главное окно поищет само и покажет ошибку в логе
            }

            await delay;

            MainWindow main = new MainWindow { PreloadedRoots = roots };
            Application.Current.MainWindow = main;
            main.Show();
            main.Activate();

            Topmost = false;
            DoubleAnimation fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }

        private static void Animate(IAnimatable target, DependencyProperty dp, double from, double to, int ms)
        {
            target.BeginAnimation(dp, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }
}
