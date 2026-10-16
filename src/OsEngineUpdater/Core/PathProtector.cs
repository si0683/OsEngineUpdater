using System.Text;
using System.Text.RegularExpressions;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Защищённые пути (маски в стиле glob: *, **, ?). Файлы под защитой не пишутся и не удаляются.
    /// </summary>
    public sealed class PathProtector
    {
        private readonly List<Regex> _rules = new List<Regex>();

        public PathProtector(IEnumerable<string> globs)
        {
            foreach (string g in globs)
            {
                if (!string.IsNullOrWhiteSpace(g))
                {
                    _rules.Add(new Regex(GlobToRegex(g.Trim()), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
            }
        }

        public static PathProtector FromConfig(UpdaterConfig cfg)
        {
            List<string> globs = new List<string> { "**/obj/**", "**/.vs/**", "**/.git/**", "**/*.user" };

            if (cfg.BinProtected)
            {
                globs.Add("**/bin/**");
            }

            if (cfg.ProtectedPaths != null)
            {
                globs.AddRange(cfg.ProtectedPaths);
            }

            globs.AddRange(cfg.UserProtectedPaths);

            return new PathProtector(globs);
        }

        public bool IsProtected(string relativePath)
        {
            string p = relativePath.Replace('\\', '/');

            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].IsMatch(p))
                {
                    return true;
                }
            }

            return false;
        }

        public static string GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder("^");
            string norm = glob.Replace('\\', '/');

            for (int i = 0; i < norm.Length; i++)
            {
                char c = norm[i];

                if (c == '*')
                {
                    if (i + 1 < norm.Length && norm[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;

                        if (i + 1 < norm.Length && norm[i + 1] == '/')
                        {
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }

            sb.Append('$');
            return sb.ToString();
        }
    }
}
