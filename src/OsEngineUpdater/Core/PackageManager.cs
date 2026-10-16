using System.Text.Json;

namespace OsEngineUpdater.Core
{
    /// <summary>Что из ваших репозиториев установлено в копию (&lt;копия&gt;\.osengine-updater\packages.json).</summary>
    public sealed class InstalledPackage
    {
        /// <summary>Ключ установки: репозиторий + тип (+ папка для своих файлов).</summary>
        public string SourceId { get; set; }

        /// <summary>Ключ репозитория (нормализованный адрес).</summary>
        public string RepoId { get; set; }

        public string Name { get; set; }
        public PackageKind Kind { get; set; }
        public string Url { get; set; }
        public string Branch { get; set; }
        public string Commit { get; set; }
        public string InstalledUtc { get; set; }

        /// <summary>Выбранные файлы репозитория (пути через /). null — весь репозиторий.</summary>
        public List<string> Selected { get; set; }

        /// <summary>Папка установки относительно корня копии.</summary>
        public string TargetDir { get; set; }

        /// <summary>Файлы относительно корня копии (через /) → git-blob-SHA на момент установки.</summary>
        public Dictionary<string, string> Files { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PackageResult
    {
        public bool Done { get; init; }
        public string Message { get; init; }
    }

    /// <summary>Скачанный (клонированный) репозиторий во временной папке.</summary>
    public sealed class RepoCheckout : IDisposable
    {
        public RepoInfo Repo { get; init; }
        public string Dir { get; init; }
        public string Commit { get; init; }

        /// <summary>Файлы репозитория (пути через /) без служебных файлов git.</summary>
        public List<string> Files { get; init; }

        public void Dispose() => PackageManager.DeleteTree(Dir);
    }

    /// <summary>
    /// Установка / обновление / удаление выбранного из своих репозиториев.
    /// Репозиторий клонируется во временную папку, выбранные файлы копируются в копию OsEngine, установка запоминается.
    /// Установленные файлы защищены от обновления OsEngine (даже если лежат поверх официальных).
    /// </summary>
    public static class PackageManager
    {
        private static readonly string[] SkipNames = { ".git", ".github", ".gitignore", ".gitattributes", ".gitmodules" };

        #region Installed list

        private static string StorePath(CopyInfo copy) => Path.Combine(copy.DataDir, "packages.json");

        public static List<InstalledPackage> LoadInstalled(CopyInfo copy)
        {
            try
            {
                if (File.Exists(StorePath(copy)))
                {
                    List<InstalledPackage> list = JsonSerializer.Deserialize<List<InstalledPackage>>(File.ReadAllText(StorePath(copy)));

                    if (list != null)
                    {
                        foreach (InstalledPackage p in list)
                        {
                            p.Files = new Dictionary<string, string>(p.Files ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                            p.RepoId ??= RepoInfo.NormalizeId(p.Url);
                        }

                        return list;
                    }
                }
            }
            catch
            {
                // испорченный список — считаем, что ничего не установлено
            }

            return new List<InstalledPackage>();
        }

        private static void SaveInstalled(CopyInfo copy, List<InstalledPackage> list)
        {
            Directory.CreateDirectory(copy.DataDir);
            File.WriteAllText(StorePath(copy), JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }

        /// <summary>Все файлы, установленные из своих репозиториев (для защиты от обновления OsEngine).</summary>
        public static IEnumerable<string> InstalledFiles(CopyInfo copy)
        {
            return LoadInstalled(copy).SelectMany(p => p.Files.Keys);
        }

        public static string InstallKey(RepoInfo repo, PackageKind kind, string targetRel)
        {
            return repo.Id + "|" + kind + (kind == PackageKind.Files ? "|" + (targetRel ?? "").ToLowerInvariant() : "");
        }

        #endregion

        /// <summary>Папка установки (абсолютный путь). Для робота/индикатора — подпапка с именем репозитория.</summary>
        public static string TargetDir(CopyInfo copy, UpdaterConfig cfg, PackageKind kind, string repoName, string filesTarget)
        {
            string rel = kind switch
            {
                PackageKind.Robot => Path.Combine(cfg.EffectiveRobotsDir, SafeName(repoName)),
                PackageKind.Indicator => Path.Combine(cfg.EffectiveIndicatorsDir, SafeName(repoName)),
                _ => (filesTarget ?? "").Trim()
            };

            string full = Path.GetFullPath(Path.Combine(copy.Root, rel.TrimStart('\\', '/')));
            string root = copy.Root.TrimEnd('\\') + "\\";

            if (!(full + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Папка установки выходит за пределы копии: {rel}");
            }

            return full.TrimEnd('\\');
        }

        /// <summary>Скачать репозиторий (последний коммит) во временную папку.</summary>
        public static async Task<RepoCheckout> FetchAsync(RepoInfo repo, CancellationToken ct)
        {
            string temp = Path.Combine(Path.GetTempPath(), "osu_pkg_" + Guid.NewGuid().ToString("N"));
            List<string> args = new List<string> { "clone", "--depth", "1", "--quiet" };

            if (!string.IsNullOrWhiteSpace(repo.Branch))
            {
                args.Add("--branch");
                args.Add(repo.Branch.Trim());
            }

            args.Add(repo.CloneUrl);
            args.Add(temp);

            (int code, _, string err) = await GitRunner.RunWithAuthAsync(null, RepoCatalog.GitAuthHeader(repo), ct, args.ToArray());

            if (code != 0)
            {
                DeleteTree(temp);
                throw new InvalidOperationException(GitRunner.ShortError(err));
            }

            (_, string sha, _) = await GitRunner.RunAsync(temp, ct, "rev-parse", "HEAD");

            List<string> files = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(temp, f))
                .Where(rel => !rel.Split('\\').Any(seg => SkipNames.Contains(seg, StringComparer.OrdinalIgnoreCase)))
                .Select(rel => rel.Replace('\\', '/'))
                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new RepoCheckout { Repo = repo, Dir = temp, Commit = sha, Files = files };
        }

        /// <summary>
        /// Установить или обновить выбранные файлы репозитория. selected = null — весь репозиторий.
        /// confirm(текст) вызывается, если будут перезаписаны чужие или изменённые вами файлы.
        /// </summary>
        public static PackageResult Install(CopyInfo copy, UpdaterConfig cfg, RepoCheckout co, PackageKind kind, string filesTarget,
            IReadOnlyCollection<string> selected, UpdateLog log, Func<string, bool> confirm)
        {
            RepoInfo repo = co.Repo;

            try
            {
                string target = TargetDir(copy, cfg, kind, repo.Name, filesTarget);
                string targetRel = Rel(copy, target);
                string key = InstallKey(repo, kind, targetRel);

                HashSet<string> pick = selected == null ? null : new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                List<string> files = co.Files.Where(f => pick == null || pick.Contains(f)).ToList();

                if (files.Count == 0)
                {
                    return Fail(log, repo.Name, "не выбрано ни одного файла.");
                }

                List<InstalledPackage> installed = LoadInstalled(copy);
                InstalledPackage prev = installed.FirstOrDefault(p => p.SourceId == key);

                // проверка перезаписи: чужие файлы и файлы, изменённые после прошлой установки
                List<string> foreign = new List<string>();
                List<string> modified = new List<string>();

                foreach (string f in files)
                {
                    string src = Path.Combine(co.Dir, f.Replace('/', '\\'));
                    string dest = Path.Combine(target, f.Replace('/', '\\'));
                    string destKey = Rel(copy, dest);

                    if (!File.Exists(dest) || UpdateEngine.GitBlobSha(dest) == UpdateEngine.GitBlobSha(src))
                    {
                        continue;
                    }

                    if (prev != null && prev.Files.TryGetValue(destKey, out string was))
                    {
                        if (UpdateEngine.GitBlobSha(dest) != was)
                        {
                            modified.Add(destKey);
                        }
                    }
                    else
                    {
                        foreign.Add(destKey);
                    }
                }

                if (foreign.Count + modified.Count > 0)
                {
                    string msg = $"«{repo.Name}»: будут перезаписаны файлы в {Shown(targetRel)}:\n";

                    if (modified.Count > 0)
                    {
                        msg += $"\nИзменены вами после прошлой установки ({modified.Count}):\n  " + string.Join("\n  ", modified.Take(10));
                    }

                    if (foreign.Count > 0)
                    {
                        msg += $"\nНе из этого репозитория ({foreign.Count}):\n  " + string.Join("\n  ", foreign.Take(10));
                    }

                    if (foreign.Count + modified.Count > 20)
                    {
                        msg += "\n  ...";
                    }

                    if (!confirm(msg + "\n\nПерезаписать?"))
                    {
                        log.Warn($"{repo.Name}: установка отменена.");
                        return new PackageResult { Done = false, Message = "отменено" };
                    }
                }

                InstalledPackage rec = new InstalledPackage
                {
                    SourceId = key,
                    RepoId = repo.Id,
                    Name = repo.Name,
                    Kind = kind,
                    Url = repo.WebUrl ?? repo.CloneUrl,
                    Branch = repo.Branch,
                    Commit = co.Commit,
                    InstalledUtc = DateTime.UtcNow.ToString("o"),
                    Selected = pick == null ? null : files,
                    TargetDir = targetRel
                };

                foreach (string f in files)
                {
                    string dest = Path.Combine(target, f.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(Path.Combine(co.Dir, f.Replace('/', '\\')), dest, true);
                    File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
                    rec.Files[Rel(copy, dest)] = UpdateEngine.GitBlobSha(dest);
                    log.Detail("  + " + Rel(copy, dest));
                }

                // файлы прошлой установки, которых больше нет в выборе или в репозитории
                if (prev != null)
                {
                    foreach (KeyValuePair<string, string> old in prev.Files)
                    {
                        if (rec.Files.ContainsKey(old.Key))
                        {
                            continue;
                        }

                        string path = Path.Combine(copy.Root, old.Key.Replace('/', '\\'));

                        if (File.Exists(path) && UpdateEngine.GitBlobSha(path) == old.Value)
                        {
                            File.Delete(path);
                            RemoveEmptyDirs(target, Path.GetDirectoryName(path));
                            log.Detail("  x " + old.Key);
                        }
                    }

                    installed.Remove(prev);
                }

                installed.Add(rec);
                SaveInstalled(copy, installed);

                string verb = prev == null ? "установлен" : (prev.Commit == co.Commit ? "переустановлен" : "обновлён");
                string what = pick == null ? "весь репозиторий" : $"выбрано файлов: {files.Count}";
                log.Ok($"{repo.Name}: {verb} ({UpdateEngine.Short(co.Commit)}, {what}) → {Shown(targetRel)}.");
                return new PackageResult { Done = true, Message = verb };
            }
            catch (Exception ex)
            {
                return Fail(log, repo.Name, ex.Message);
            }
        }

        /// <summary>Удалить установленное из копии. Изменённые вами файлы удаляются только с подтверждения.</summary>
        public static PackageResult Uninstall(CopyInfo copy, InstalledPackage pkg, UpdateLog log, Func<string, bool> confirm)
        {
            List<string> modified = pkg.Files
                .Where(f =>
                {
                    string p = Path.Combine(copy.Root, f.Key.Replace('/', '\\'));
                    return File.Exists(p) && UpdateEngine.GitBlobSha(p) != f.Value;
                })
                .Select(f => f.Key)
                .ToList();

            bool deleteModified = true;

            if (modified.Count > 0)
            {
                deleteModified = confirm($"«{pkg.Name}»: эти файлы вы изменили после установки:\n  "
                                         + string.Join("\n  ", modified.Take(10))
                                         + (modified.Count > 10 ? "\n  ..." : "")
                                         + "\n\nУдалить и их? («Нет» — оставить изменённые файлы)");
            }

            int removed = 0;
            string boundary = CleanupBoundary(copy, pkg.Kind, pkg.TargetDir);

            foreach (string key in pkg.Files.Keys)
            {
                if (!deleteModified && modified.Contains(key))
                {
                    continue;
                }

                string path = Path.Combine(copy.Root, key.Replace('/', '\\'));

                if (File.Exists(path))
                {
                    File.Delete(path);
                    RemoveEmptyDirs(boundary, Path.GetDirectoryName(path));
                    log.Detail("  x " + key);
                    removed++;
                }
            }

            List<InstalledPackage> installed = LoadInstalled(copy);
            installed.RemoveAll(p => p.SourceId == pkg.SourceId);
            SaveInstalled(copy, installed);

            string hint = pkg.Kind == PackageKind.Files
                ? " Если файлы лежали поверх файлов OsEngine — нажмите «Обновить сейчас», чтобы вернуть официальные."
                : "";

            log.Ok($"{pkg.Name}: удалён из копии, файлов {removed}.{hint}");
            return new PackageResult { Done = true, Message = "удалён" };
        }

        #region Helpers

        public static string SafeName(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string s = new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
            return s.Length == 0 ? "Package" : s;
        }

        public static string Rel(CopyInfo copy, string full)
        {
            string rel = Path.GetRelativePath(copy.Root, full).Replace('\\', '/');
            return rel == "." ? "" : rel;
        }

        public static string Shown(string rel) => string.IsNullOrEmpty(rel) ? "корень копии" : rel;

        /// <summary>
        /// Граница, выше которой пустые папки не удаляются: для робота/индикатора — папка над ним
        /// (его собственная папка удаляется), для своих файлов — сама целевая папка.
        /// </summary>
        private static string CleanupBoundary(CopyInfo copy, PackageKind kind, string targetRel)
        {
            string target = Path.Combine(copy.Root, (targetRel ?? "").Replace('/', '\\')).TrimEnd('\\');
            return kind == PackageKind.Files ? target : Path.GetDirectoryName(target);
        }

        private static void RemoveEmptyDirs(string boundary, string dir)
        {
            string root = boundary.TrimEnd('\\');

            while (!string.IsNullOrEmpty(dir) && dir.Length > root.Length
                   && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        /// <summary>Удаление клона: файлы .git бывают только для чтения.</summary>
        public static void DeleteTree(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return;
                }

                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, true);
            }
            catch
            {
                // временная папка — не критично
            }
        }

        private static PackageResult Fail(UpdateLog log, string name, string message)
        {
            log.Error($"{name}: {message}");
            return new PackageResult { Done = false, Message = message };
        }

        #endregion
    }
}
