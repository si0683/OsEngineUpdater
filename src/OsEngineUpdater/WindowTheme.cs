using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OsEngineUpdater
{
    /// <summary>Системная плашка заголовка в цвет темы (Windows 10 — тёмная, Windows 11 — цвет фона окна).</summary>
    public static class WindowTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Вызывать в конструкторе окна: применится, как только у окна появится handle.</summary>
        public static void Apply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;

            if (hwnd != IntPtr.Zero)
            {
                ApplyTo(hwnd);
                return;
            }

            window.SourceInitialized += (_, _) => ApplyTo(new WindowInteropHelper(window).Handle);
        }

        private static void ApplyTo(IntPtr hwnd)
        {
            try
            {
                int on = 1;

                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                {
                    // Windows 10 до 20H1
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
                }

                // Windows 11: точные цвета темы; на Windows 10 эти вызовы просто ничего не делают
                int caption = ToColorRef(ThemeColor("Bg"));
                int text = ToColorRef(ThemeColor("Fg"));
                int border = ToColorRef(ThemeColor("Line"));
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private static Color ThemeColor(string key)
        {
            return Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : Colors.Black;
        }

        private static int ToColorRef(Color c)
        {
            return c.R | c.G << 8 | c.B << 16;
        }
    }
}
