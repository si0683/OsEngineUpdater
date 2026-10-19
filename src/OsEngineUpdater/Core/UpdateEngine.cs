using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OsEngineUpdater.Core
{
    public enum UpdateMode
    {
        /// <summary>Полная сверка с деревом GitHub: добавить / обновить / удалить официальные файлы.</summary>
        Mirror,
        /// <summary>Только изменения между прошлым и новым коммитом.</summary>
        Delta,
        /// <summary>Полная перекачка из архива.</summary>
        Full,
        /// <summary>Только проверить, ничего не менять.</summary>
        Check
    }

    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Error = 1;
        public const int SkippedRunning = 2;
        public const int Busy = 3;
        public const int Cancelled = 4;
    }

    /// <summary>
    /// Обновление одной копии OsEngine из официального репозитория.
    /// Правило: пишутся и удаляются только файлы, которые есть в официальном репозитории.
    /// </summary>
    public sealed class UpdateEngine : IDisposable
    {
        private const int DownloadThreads = 8;
        private const int CompareFilesLimit = 300;

        private readonly CopyInfo _copy;
        private readonly UpdaterConfig _cfg;
        private readonly UpdateLog _log;
        private readonly PathProtector _protector;
        private readonly GitHubClient _gh;

        private static readonly ConcurrentDictionary<string, int> AheadCache = new ConcurrentDictionary<string, int>();

        /// <summary>Последний коммит ветки (для общей проверки нескольких копий).</summary>
        public Task<string> GetHeadShaAsync(CancellationToken ct) => _gh.GetHeadShaAsync(ct);

        /// <summary>Обновлять даже если OsEngine этой копии запущен.</summary>
        public bool Force { get; set; }

        /// <summary>Не запускать сборку после обновления.</summary>
        public bool NoBuild { get; set; }

        public UpdateEngine(CopyInfo copy, UpdaterConfig cfg, UpdateLog log)
        {
            _copy = copy;
            _cfg = cfg;
            _log = log;
            _protector = PathProtector.FromConfig(cfg, PackageManager.InstalledFiles(copy));
            _gh = new GitHubClient(cfg);
        }

        public async Task<int> RunAsync(UpdateMode mode, CancellationToken ct)
        {
            // одна операция на копию одновременно (окно и Планировщик)
            using Semaphore guard = new Semaphore(1, 1, "Local\\" + _copy.TaskName);

            if (!guard.WaitOne(0))
            {
                _log.Warn("Для этой копии уже выполняется обновление — пропуск.");
                return ExitCodes.Busy;
            }

            try
            {
                _log.Info($"Копия: {_copy.Root}");
                _log.Info($"Источник: {_gh.Source} (профиль: {_cfg.Profile}, сборка: {(_cfg.ShouldBuild && !NoBuild ? "да" : "нет")})");

                if (mode != UpdateMode.Check && IsCopyRunning())
                {
                    if (Force)
                    {
                        _log.Warn("OsEngine этой копии запущен, но включено «обновлять при запущенном OsEngine» — продолжаю.");
                    }
                    else
                    {
                        _log.Warn("OsEngine этой копии запущен — обновление пропущено. Закройте терминал и повторите.");
                        return ExitCodes.SkippedRunning;
                    }
                }

                bool changed;

                switch (mode)
                {
                    case UpdateMode.Check:
                        await CheckAsync(ct);
                        return ExitCodes.Ok;
                    case UpdateMode.Delta:
                        changed = await DeltaPassAsync(ct);
                        break;
                    case UpdateMode.Full:
                        changed = await FullSyncAsync(ct);
                        break;
                    default:
                        changed = await MirrorAsync(true, ct);
                        break;
                }

                if (changed || mode == UpdateMode.Full)
                {
                    await BuildAsync(ct);
                }

                return ExitCodes.Ok;
            }
            catch (OperationCanceledException)
            {
                _log.Warn("Операция отменена.");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                _log.Error("Ошибка: " + ex.Message);
                return ExitCodes.Error;
            }
            finally
            {
                guard.Release();
            }
        }

        /// <summary>На сколько коммитов копия отстаёт от GitHub (по state.json). null — неизвестно.</summary>
        /// <param name="knownHead">Последний коммит, если уже известен (одна проверка на несколько копий с общим источником).</param>
        public async Task<(int? Behind, string HeadSha)> GetBehindAsync(CancellationToken ct, string knownHead = null)
        {
            string head = knownHead ?? await _gh.GetHeadShaAsync(ct);
            UpdaterState state = _copy.LoadState();

            if (state == null || string.IsNullOrEmpty(state.LastSha))
            {
                return (null, head);
            }

            if (string.Equals(state.LastSha, head, StringComparison.OrdinalIgnoreCase))
            {
                return (0, head);
            }

            // результат сравнения двух коммитов не меняется — запоминаем, чтобы не тратить лимит API
            string key = _gh.Source + ":" + state.LastSha + ".." + head;

            if (!AheadCache.TryGetValue(key, out int ahead))
            {
                ahead = (await _gh.CompareAsync(state.LastSha, head, ct)).AheadBy;
                AheadCache[key] = ahead;
            }

            return (ahead, head);
        }

        #region Check

        private async Task CheckAsync(CancellationToken ct)
        {
            (int? behind, string head) = await GetBehindAsync(ct);

            if (behind == null)
            {
                _log.Warn("Копия ещё ни разу не обновлялась этой утилитой.");
            }
            else if (behind == 0)
            {
                _log.Ok($"Последний коммит уже применён ({Short(head)}).");
            }
            else
            {
                _log.Warn($"Доступно обновление: {behind} {Plural(behind.Value, "коммит", "коммита", "коммитов")} ({Short(_copy.LoadState()?.LastSha)} → {Short(head)}).");
            }

            await MirrorAsync(false, ct, head);
        }

        #endregion

        #region Mirror

        /// <returns>true — что-то изменилось.</returns>
        private async Task<bool> MirrorAsync(bool apply, CancellationToken ct, string head = null)
        {
            head ??= await _gh.GetHeadShaAsync(ct);
            _log.Info($"Последний коммит: {head}");

            (List<TreeEntry> blobs, bool truncated) = await _gh.GetTreeAsync(head, ct);

            if (truncated)
            {
                throw new GitHubException("GitHub отдал неполное дерево файлов — режим Mirror невозможен. Используйте полную перекачку.");
            }

            _log.Info($"Официальных файлов: {blobs.Count}");

            HashSet<string> officialSet = new HashSet<string>(blobs.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
            List<string> managed = new List<string>();
            int skipped = 0;

            foreach (TreeEntry b in blobs)
            {
                if (_protector.IsProtected(b.Path))
                {
                    skipped++;
                }
                else
                {
                    managed.Add(b.Path);
                }
            }

            // сверка локальных файлов по git-blob-SHA (параллельно)
            ConcurrentBag<TreeEntry> toAdd = new ConcurrentBag<TreeEntry>();
            ConcurrentBag<TreeEntry> toUpdate = new ConcurrentBag<TreeEntry>();
            Dictionary<string, TreeEntry> byPath = blobs.ToDictionary(b => b.Path, StringComparer.OrdinalIgnoreCase);

            Parallel.ForEach(managed, new ParallelOptions { CancellationToken = ct }, path =>
            {
                TreeEntry b = byPath[path];
                string local = LocalPath(path);

                if (!File.Exists(local))
                {
                    toAdd.Add(b);
                }
                else if (!string.Equals(GitBlobSha(local), b.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    toUpdate.Add(b);
                }
            });

            List<string> toRemove = _copy.LoadManifest()
                .Where(p => !_protector.IsProtected(p) && !officialSet.Contains(p) && File.Exists(LocalPath(p)))
                .ToList();

            int same = managed.Count - toAdd.Count - toUpdate.Count;

            if (!apply)
            {
                foreach (TreeEntry b in toAdd.OrderBy(x => x.Path).Take(100)) _log.Detail("  + " + b.Path);
                foreach (TreeEntry b in toUpdate.OrderBy(x => x.Path).Take(100)) _log.Detail("  ~ " + b.Path);
                foreach (string p in toRemove.Take(100)) _log.Detail("  x " + p);

                int total = toAdd.Count + toUpdate.Count + toRemove.Count;

                if (total == 0)
                {
                    _log.Ok($"Файлы совпадают с репозиторием ({same} шт., защищено {skipped}).");
                }
                else
                {
                    _log.Warn($"Отличается файлов: {total} (добавить {toAdd.Count}, обновить {toUpdate.Count}, удалить {toRemove.Count}). Нажмите «Обновить сейчас».");
                }

                return false;
            }

            int failed = 0;
            failed += await DownloadAllAsync(toAdd.OrderBy(x => x.Path).ToList(), head, "+", ct);
            failed += await DownloadAllAsync(toUpdate.OrderBy(x => x.Path).ToList(), head, "~", ct);

            foreach (string p in toRemove)
            {
                if (DeleteLocal(p))
                {
                    _log.Detail("  x " + p);
                }
            }

            _copy.SaveState(head);
            _copy.SaveManifest(managed);
            await SyncBuiltInUpdaterStateAsync(head, ct);

            string summary = $"Готово: добавлено {toAdd.Count}, обновлено {toUpdate.Count}, без изменений {same}, удалено {toRemove.Count}, защищено {skipped}";

            if (failed > 0)
            {
                _log.Warn(summary + $", ошибок {failed} (будут докачаны при следующем запуске).");
            }
            else
            {
                _log.Ok(summary + ".");
            }

            return toAdd.Count + toUpdate.Count + toRemove.Count > 0;
        }

        #endregion

        #region Delta

        private async Task<bool> DeltaPassAsync(CancellationToken ct)
        {
            string head = await _gh.GetHeadShaAsync(ct);
            _log.Info($"Последний коммит: {head}");
            UpdaterState state = _copy.LoadState();
            string baseSha = state?.LastSha;

            if (string.IsNullOrEmpty(baseSha))
            {
                _log.Warn("Нет сохранённого состояния — выполняю полную сверку (Mirror).");
                return await MirrorAsync(true, ct, head);
            }

            if (string.Equals(baseSha, head, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(_copy.ManifestPath))
                {
                    (List<TreeEntry> b, bool tr) = await _gh.GetTreeAsync(head, ct);

                    if (!tr)
                    {
                        _copy.SaveManifest(b.Select(x => x.Path).Where(p => !_protector.IsProtected(p)));
                    }
                }

                _log.Ok($"Уже актуально ({Short(head)}).");
                return false;
            }

            _log.Info($"Сравнение {Short(baseSha)} → {Short(head)} ...");
            CompareResult cmp = await _gh.CompareAsync(baseSha, head, ct);

            if (cmp.Files.Count >= CompareFilesLimit)
            {
                _log.Warn($"Изменений слишком много для Delta ({cmp.Files.Count}+) — выполняю полную сверку (Mirror).");
                return await MirrorAsync(true, ct, head);
            }

            (List<TreeEntry> blobs, bool truncated) = await _gh.GetTreeAsync(head, ct);
            HashSet<string> officialSet = truncated ? null : new HashSet<string>(blobs.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);

            if (officialSet != null)
            {
                Prune(officialSet);
            }

            _log.Info($"Изменённых файлов в репозитории: {cmp.Files.Count}");
            List<TreeEntry> download = new List<TreeEntry>();
            int n = 0;
            int skip = 0;

            foreach (ChangedFile f in cmp.Files)
            {
                if (f.Status == "renamed" && f.PreviousFileName != null && !_protector.IsProtected(f.PreviousFileName))
                {
                    DeleteLocal(f.PreviousFileName);
                }

                if (_protector.IsProtected(f.FileName)
                    || (officialSet != null && f.Status != "removed" && !officialSet.Contains(f.FileName)))
                {
                    skip++;
                    continue;
                }

                if (f.Status == "removed")
                {
                    if (DeleteLocal(f.FileName))
                    {
                        n++;
                        _log.Detail("  - " + f.FileName);
                    }

                    continue;
                }

                download.Add(new TreeEntry { Path = f.FileName });
            }

            int failed = await DownloadAllAsync(download, head, "+", ct);
            n += download.Count - failed;

            if (failed > 0)
            {
                // не сдвигаем базу, чтобы при следующем запуске недокачанное повторилось
                _log.Warn($"Применено {n}, ошибок {failed}, защищено {skip}. Состояние не сохранено — повторите позже.");
                return n > 0;
            }

            _copy.SaveState(head);

            if (officialSet != null)
            {
                _copy.SaveManifest(officialSet.Where(p => !_protector.IsProtected(p)));
            }

            await SyncBuiltInUpdaterStateAsync(head, ct);
            _log.Ok($"Обновлено до {Short(head)}: применено {n}, защищено {skip}.");
            return n > 0;
        }

        private void Prune(HashSet<string> officialSet)
        {
            int removed = 0;

            foreach (string p in _copy.LoadManifest())
            {
                if (_protector.IsProtected(p) || officialSet.Contains(p))
                {
                    continue;
                }

                if (DeleteLocal(p))
                {
                    _log.Detail("  x " + p);
                    removed++;
                }
            }

            if (removed > 0)
            {
                _log.Ok($"Удалено файлов, убранных из репозитория: {removed}.");
            }
        }

        #endregion

        #region Full sync and bootstrap

        private async Task<bool> FullSyncAsync(CancellationToken ct)
        {
            string head = await _gh.GetHeadShaAsync(ct);
            (List<TreeEntry> blobs, bool truncated) = await _gh.GetTreeAsync(head, ct);
            HashSet<string> officialSet = truncated ? null : new HashSet<string>(blobs.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);

            if (officialSet != null)
            {
                Prune(officialSet);
            }

            int copied = 0;
            int skipped = 0;

            await WithExtractedArchiveAsync(_gh, _log, ct, archiveRoot =>
            {
                foreach (string file in Directory.EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = Path.GetRelativePath(archiveRoot, file);

                    if (_protector.IsProtected(rel))
                    {
                        skipped++;
                        continue;
                    }

                    string dest = Path.Combine(_copy.Root, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, true);
                    copied++;
                }
            });

            _copy.SaveState(head);

            if (officialSet != null)
            {
                _copy.SaveManifest(officialSet.Where(p => !_protector.IsProtected(p)));
            }

            await SyncBuiltInUpdaterStateAsync(head, ct);
            _log.Ok($"Полная перекачка завершена: скопировано {copied}, защищено {skipped}.");
            return true;
        }

        /// <summary>Создать новую копию OsEngine из архива репозитория в папке target.</summary>
        public static async Task<int> BootstrapAsync(string target, UpdaterConfig cfg, Action<LogLevel, string> sink, CancellationToken ct)
        {
            UpdateLog tmpLog = new UpdateLog(null, sink);

            try
            {
                Directory.CreateDirectory(target);

                if (Directory.EnumerateFileSystemEntries(target).Any())
                {
                    tmpLog.Warn($"Папка не пуста: {target} (официальные файлы будут перезаписаны, ваши — сохранены).");
                }

                using (GitHubClient gh = new GitHubClient(cfg))
                {
                    await WithExtractedArchiveAsync(gh, tmpLog, ct, archiveRoot =>
                    {
                        foreach (string file in Directory.EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories))
                        {
                            ct.ThrowIfCancellationRequested();
                            string dest = Path.Combine(target, Path.GetRelativePath(archiveRoot, file));
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            File.Copy(file, dest, true);
                        }
                    });
                }

                tmpLog.Ok($"Файлы распакованы в {target}");

                CopyInfo copy = new CopyInfo(target);

                if (!CopyInfo.IsCopyRoot(copy.Root))
                {
                    tmpLog.Error("В архиве не найден project\\OsEngine\\OsEngine.csproj.");
                    return ExitCodes.Error;
                }

                cfg.Save(copy);
                UpdateLog log = new UpdateLog(copy.LogPath, sink);

                using UpdateEngine engine = new UpdateEngine(copy, cfg, log);
                string head = await engine._gh.GetHeadShaAsync(ct);
                (List<TreeEntry> blobs, bool truncated) = await engine._gh.GetTreeAsync(head, ct);
                copy.SaveState(head);

                if (!truncated)
                {
                    copy.SaveManifest(blobs.Select(b => b.Path).Where(p => !engine._protector.IsProtected(p)));
                }

                await engine.SyncBuiltInUpdaterStateAsync(head, ct);
                await engine.BuildAsync(ct);
                log.Ok("Новая копия создана.");
                return ExitCodes.Ok;
            }
            catch (OperationCanceledException)
            {
                tmpLog.Warn("Операция отменена.");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                tmpLog.Error("Ошибка: " + ex.Message);
                return ExitCodes.Error;
            }
        }

        private static async Task WithExtractedArchiveAsync(GitHubClient gh, UpdateLog log, CancellationToken ct, Action<string> useRoot)
        {
            string zip = Path.Combine(Path.GetTempPath(), "osengine_" + Guid.NewGuid().ToString("N") + ".zip");
            string dst = Path.Combine(Path.GetTempPath(), "osengine_" + Guid.NewGuid().ToString("N"));

            try
            {
                log.Warn("Скачиваю архив репозитория (около 200 МБ, подождите)...");
                await gh.DownloadArchiveAsync(zip, ct);
                log.Info("Распаковка...");
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, dst), ct);

                string archiveRoot = Directory.GetDirectories(dst).FirstOrDefault()
                                     ?? throw new InvalidOperationException("В архиве не найдена корневая папка.");

                await Task.Run(() => useRoot(archiveRoot), ct);
            }
            finally
            {
                TryDelete(() => File.Delete(zip));
                TryDelete(() => Directory.Delete(dst, true));
            }
        }

        #endregion

        #region Build

        private async Task BuildAsync(CancellationToken ct)
        {
            if (NoBuild || !_cfg.ShouldBuild)
            {
                return;
            }

            string cmd = _cfg.EffectiveBuildCommand;
            _log.Info("Сборка: " + cmd);

            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                WorkingDirectory = _copy.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "ru";

            using Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Detail(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Detail(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(() => p.Kill(true));
                throw;
            }

            if (p.ExitCode == 0)
            {
                _log.Ok("Сборка успешна.");
            }
            else
            {
                _log.Error($"Сборка завершилась с кодом {p.ExitCode}.");
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Синхронизирует счётчик встроенного обновлятора OsEngine (кнопка «Обновить (N)»)
        /// с применённым коммитом. Файл LastUpdatesInfo.txt не входит в репозиторий.
        /// </summary>
        private async Task SyncBuiltInUpdaterStateAsync(string sha, CancellationToken ct)
        {
            try
            {
                string file = _copy.BuiltInUpdaterStatePath;

                if (!File.Exists(file))
                {
                    return;
                }

                DateTime? dt = await _gh.GetCommitDateUtcAsync(sha, ct);

                if (dt == null)
                {
                    return;
                }

                string iso = DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc).ToString("O");
                File.WriteAllText(file, iso, Encoding.ASCII);
                _log.Info("Счётчик встроенного обновлятора OsEngine синхронизирован: " + iso);
            }
            catch (Exception ex)
            {
                _log.Warn("Не удалось синхронизировать счётчик встроенного обновлятора: " + ex.Message);
            }
        }

        private async Task<int> DownloadAllAsync(List<TreeEntry> files, string sha, string mark, CancellationToken ct)
        {
            int failed = 0;

            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = DownloadThreads, CancellationToken = ct },
                async (f, token) =>
                {
                    try
                    {
                        byte[] data = await _gh.DownloadFileAsync(f.Path, sha, token);
                        string local = LocalPath(f.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(local));
                        await File.WriteAllBytesAsync(local, data, token);
                        _log.Detail($"  {mark} {f.Path}");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _log.Error($"  ! {f.Path}: {ex.Message}");
                    }
                });

            return failed;
        }

        private string LocalPath(string repoPath)
        {
            return Path.Combine(_copy.Root, repoPath.Replace('/', '\\'));
        }

        private bool DeleteLocal(string repoPath)
        {
            string local = LocalPath(repoPath);

            if (!File.Exists(local))
            {
                return false;
            }

            try
            {
                File.Delete(local);
                RemoveEmptyDirs(Path.GetDirectoryName(local));
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"  ! не удалось удалить {repoPath}: {ex.Message}");
                return false;
            }
        }

        private void RemoveEmptyDirs(string dir)
        {
            string root = _copy.Root.TrimEnd('\\');

            while (!string.IsNullOrEmpty(dir)
                   && dir.Length > root.Length
                   && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(dir)
                   && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        /// <summary>git-blob-SHA файла (как считает git: sha1("blob &lt;size&gt;\0" + содержимое)).</summary>
        public static string GitBlobSha(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            h.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0"));
            h.AppendData(bytes);
            return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        }

        public bool IsCopyRunning()
        {
            string prefix = _copy.Root.TrimEnd('\\') + "\\";

            foreach (Process p in Process.GetProcessesByName("OsEngine"))
            {
                try
                {
                    string path = p.MainModule?.FileName;

                    if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // нет доступа к процессу — не наш
                }
                finally
                {
                    p.Dispose();
                }
            }

            return false;
        }

        public static string Short(string sha) => string.IsNullOrEmpty(sha) ? "—" : sha.Substring(0, Math.Min(8, sha.Length));

        public static string Plural(int n, string one, string few, string many)
        {
            int n10 = n % 10;
            int n100 = n % 100;

            if (n10 == 1 && n100 != 11) return one;
            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return few;
            return many;
        }

        private static void TryDelete(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // временные файлы — не критично
            }
        }

        #endregion

        public void Dispose()
        {
            _gh.Dispose();
        }
    }
}
