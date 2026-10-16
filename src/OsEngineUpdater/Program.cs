namespace OsEngineUpdater
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0)
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
