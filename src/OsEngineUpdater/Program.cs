using OsEngineUpdater.Core;

namespace OsEngineUpdater
{
    public static class Program
    {
        /// <summary>Окно запущено новой версией сразу после самообновления.</summary>
        public static bool AfterUpdate { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            // остатки прошлого самообновления (старый exe освобождается после выхода прежнего процесса)
            SelfUpdater.CleanupOld();

            AfterUpdate = args.Length == 1 && string.Equals(args[0], "--after-update", StringComparison.OrdinalIgnoreCase);

            if (args.Length > 0 && !AfterUpdate)
            {
                // режим командной строки (Планировщик, ручной запуск)
                return Cli.Run(args);
            }

            App app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
