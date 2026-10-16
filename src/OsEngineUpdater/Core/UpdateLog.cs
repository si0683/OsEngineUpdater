namespace OsEngineUpdater.Core
{
    public enum LogLevel
    {
        Info,
        Ok,
        Warn,
        Error,
        Detail
    }

    /// <summary>
    /// Лог операции: пишет в &lt;копия&gt;\.osengine-updater\update.log и передаёт строки наружу (окно / консоль).
    /// </summary>
    public sealed class UpdateLog
    {
        private const long MaxLogSize = 5 * 1024 * 1024;

        private readonly string _path;
        private readonly Action<LogLevel, string> _sink;
        private readonly object _lock = new object();

        public UpdateLog(string path, Action<LogLevel, string> sink)
        {
            _path = path;
            _sink = sink;

            if (_path != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    FileInfo fi = new FileInfo(_path);

                    if (fi.Exists && fi.Length > MaxLogSize)
                    {
                        fi.Delete();
                    }
                }
                catch
                {
                    // лог не критичен
                }
            }
        }

        public void Info(string m) => Write(LogLevel.Info, m);
        public void Ok(string m) => Write(LogLevel.Ok, m);
        public void Warn(string m) => Write(LogLevel.Warn, m);
        public void Error(string m) => Write(LogLevel.Error, m);
        public void Detail(string m) => Write(LogLevel.Detail, m);

        public void Write(LogLevel level, string message)
        {
            lock (_lock)
            {
                if (_path != null)
                {
                    try
                    {
                        File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                    }
                    catch
                    {
                        // лог не критичен
                    }
                }

                _sink?.Invoke(level, message);
            }
        }
    }
}
