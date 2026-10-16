using System.Text.Json;

namespace OsEngineUpdater.Core
{
    /// <summary>Что из ваших репозиториев установлено в копию (&lt;копия&gt;\.osengine-updater\packages.json).</summary>
    public sealed class InstalledPackage
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public PackageKind Kind { get; set; }
        public string Url { get; set; }
        public string Commit { get; set; }
        public string InstalledUtc { get; set; }

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

    /// <summary>
    /// Установка / обновление / удаление ваших роботов, индикаторов и файлов из своих репозиториев.
    /// Репозиторий клонируется во временную папку, файлы копируются в копию OsEngine, установка запоминается.
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

        #endregion

        /// <summary>Папка установки в копии (абсолютный путь). Не может выходить за пределы копии.</summary>
        public static string TargetDir(CopyInfo copy, UpdaterConfig cfg, PackageSource src)
        {
            string rel = src.Kind switch
            {
                PackageKind.Robot => Path.Combine(cfg.EffectiveRobotsDir, SafeName(src.DisplayName)),
                PackageKind.Indicator => Path.Combine(cfg.EffectiveIndicatorsDir, SafeName(src.DisplayName)),
                _ => string.IsNullOrWhiteSpace(src.TargetPath) ? "" : src.TargetPath.Trim()
            };

            string full = Path.GetFullPath(Path.Combine(copy.Root, rel.TrimStart('\\', '/')));
            string root = copy.Root.TrimEnd('\\') + "\\";

            if (!(full + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Папка установки выходит за пределы копии: {rel}");
            }

            return full.TrimEnd('\\');
        }

        /// <summary>
        /// Установить или обновить пакет. confirm(текст) вызывается, если будут перезаписаны чужие или изменённые вами файлы.
        /// </summary>
        public static async Task<PackageResult> InstallAsync(CopyInfo copy, UpdaterConfig cfg, PackageSource src,
            UpdateLog log, Func<string, bool> confirm, CancellationToken ct)
        {
            string temp = Path.Combine(Path.GetTempPath(), "osu_pkg_" + Guid.NewGuid().ToString("N"));

            try
            {
                log.Info($"{src.DisplayName}: скачиваю {src.CloneUrl}{(string.IsNullOrWhiteSpace(src.Branch) ? "" : " (" + src.Branch + ")")} ...");

                List<string> args = new List<string> { "clone", "--depth", "1", "--quiet" };

                if (!string.IsNullOrWhiteSpace(src.Branch))
                {
                    args.Add("--branch");
                    args.Add(src.Branch.Trim());
                }

                args.Add(src.CloneUrl);
                args.Add(temp);

                (int code, _, string err) = await GitRunner.RunAsync(null, ct, args.ToArray());

                if (code != 0)
                {
                    return Fail(log, src, GitRunner.ShortError(err));
                }

                (_, string sha, _) = await GitRunner.RunAsync(temp, ct, "rev-parse", "HEAD");

                string sourceDir = string.IsNullOrWhiteSpace(src.SubPath) ? temp : Path.Combine(temp, src.SubPath.Trim().TrimStart('\\', '/'));

                if (!Directory.Exists(sourceDir))
                {
                    return Fail(log, src, $"В репозитории нет папки {src.SubPath}.");
                }

                string target = TargetDir(copy, cfg, src);
                string targetRel = Rel(copy, target);

                // файлы пакета: относительный путь в пакете → полный путь в клоне
                Dictionary<string, string> files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                    .Select(f => (Full: f, Rel: Path.GetRelativePath(sourceDir, f)))
                    .Where(x => !x.Rel.Split('\\').Any(seg => SkipNames.Contains(seg, StringComparer.OrdinalIgnoreCase)))
                    .ToDictionary(x => x.Rel, x => x.Full, StringComparer.OrdinalIgnoreCase);

                if (files.Count == 0)
                {
                    return Fail(log, src, "В репозитории нет файлов для установки.");
                }

                List<InstalledPackage> installed = LoadInstalled(copy);
                InstalledPackage prev = installed.FirstOrDefault(p => p.SourceId == src.Id);

                // проверка перезаписи: чужие файлы и файлы, изменённые после прошлой установки
                List<string> foreign = new List<string>();
                List<string> modified = new List<string>();

                foreach (KeyValuePair<string, string> f in files)
                {
                    string dest = Path.Combine(target, f.Key);
                    string key = Rel(copy, dest);

                    if (!File.Exists(dest) || UpdateEngine.GitBlobSha(dest) == UpdateEngine.GitBlobSha(f.Value))
                    {
                        continue;
                    }

                    if (prev != null && prev.Files.TryGetValue(key, out string was))
                    {
                        if (UpdateEngine.GitBlobSha(dest) != was)
                        {
                            modified.Add(key);
                        }
                    }
                    else
                    {
                        foreign.Add(key);
                    }
                }

                if (foreign.Count + modified.Count > 0)
                {
                    string msg = $"«{src.DisplayName}»: будут перезаписаны файлы в {Shown(targetRel)}:\n";

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

                    msg += "\n\nПерезаписать?";

                    if (!confirm(msg))
                    {
                        log.Warn($"{src.DisplayName}: установка отменена.");
                        return new PackageResult { Done = false, Message = "отменено" };
                    }
                }

                // копирование
                InstalledPackage rec = new InstalledPackage
                {
                    SourceId = src.Id,
                    Name = src.DisplayName,
                    Kind = src.Kind,
                    Url = src.Url,
                    Commit = sha,
                    InstalledUtc = DateTime.UtcNow.ToString("o"),
                    TargetDir = targetRel
                };

                foreach (KeyValuePair<string, string> f in files)
                {
                    string dest = Path.Combine(target, f.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f.Value, dest, true);
                    File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
                    rec.Files[Rel(copy, dest)] = UpdateEngine.GitBlobSha(dest);
                    log.Detail("  + " + Rel(copy, dest));
                }

                // файлы прошлой версии, которых больше нет в репозитории
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

                string verb = prev == null ? "установлен" : (prev.Commit == sha ? "переустановлен" : "обновлён");
                log.Ok($"{src.DisplayName}: {verb} ({UpdateEngine.Short(sha)}) → {Shown(targetRel)}, файлов {rec.Files.Count}.");
                return new PackageResult { Done = true, Message = verb };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(log, src, ex.Message);
            }
            finally
            {
                DeleteTree(temp);
            }
        }

        /// <summary>Удалить установленный пакет из копии. Изменённые вами файлы удаляются только с подтверждения.</summary>
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
                    RemoveEmptyDirs(CleanupBoundary(copy, pkg.Kind, pkg.TargetDir), Path.GetDirectoryName(path));
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
            string s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
            return s.Length == 0 ? "Package" : s;
        }

        private static string Rel(CopyInfo copy, string full)
        {
            string rel = Path.GetRelativePath(copy.Root, full).Replace('\\', '/');
            return rel == "." ? "" : rel;
        }

        private static string Shown(string rel) => rel.Length == 0 ? "корень копии" : rel;

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
        private static void DeleteTree(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
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

        private static PackageResult Fail(UpdateLog log, PackageSource src, string message)
        {
            log.Error($"{src.DisplayName}: {message}");
            return new PackageResult { Done = false, Message = message };
        }

        #endregion
    }
}
