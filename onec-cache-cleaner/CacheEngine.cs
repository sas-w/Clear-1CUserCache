using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace OneCCacheCleaner
{
    public sealed class CleanupResult
    {
        public int Deleted;
        public int Failed;
        public bool Stopped;
    }

    public static class CacheEngine
    {
        private static readonly Regex GuidName = new Regex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.IgnoreCase);

        public static string[] UserRoots()
        {
            var roots = new List<string>();
            foreach (var kind in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            {
                string basePath = Environment.GetFolderPath(kind);
                if (String.IsNullOrWhiteSpace(basePath)) throw new IOException("Не удалось определить папку профиля: " + kind);
                foreach (string version in new[] { "1Cv8", "1Cv82" }) roots.Add(Path.Combine(basePath, "1C", version));
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static void AssertNoLinks(string path)
        {
            for (string cursor = Path.GetFullPath(path); !String.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Ссылка или junction: " + cursor);
        }

        public static List<string> FindCandidates(IEnumerable<string> roots)
        {
            var result = new List<string>();
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { File.GetAttributes(root); }
                catch (DirectoryNotFoundException) { continue; }
                catch (FileNotFoundException) { continue; }
                AssertNoLinks(root);
                foreach (string dir in Directory.EnumerateDirectories(root))
                    if (GuidName.IsMatch(Path.GetFileName(dir))) result.Add(dir);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static FileAttributes CheckItem(string path)
        {
            var attributes = File.GetAttributes(path);
            string name = Path.GetFileName(path);
            // 1C stores per-user software licenses in the platform conf directory
            // and shared licenses in a directory named licenses.
            bool protectedItem = name.Equals("1CEStart", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("conf", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("licenses", StringComparison.OrdinalIgnoreCase);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                string extension = Path.GetExtension(path);
                bool vrsCache = name.Equals("cache.1cd", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(Path.GetDirectoryName(path)).Equals("vrs-cache", StringComparison.OrdinalIgnoreCase);
                protectedItem |= extension.Equals(".lic", StringComparison.OrdinalIgnoreCase) ||
                    (extension.Equals(".1cd", StringComparison.OrdinalIgnoreCase) && !vrsCache);
            }
            if (protectedItem || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Защищённый объект или ссылка: " + path);
            return attributes;
        }

        private static void Preflight(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if ((CheckItem(path) & FileAttributes.Directory) != 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Preflight(child, token);
        }

        private static void DeleteTree(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AssertNoLinks(path);
            var attributes = CheckItem(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) DeleteTree(child, token);
                Directory.Delete(path, false);
            }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }

        public static CleanupResult Clean(IEnumerable<string> roots, IList<string> approved,
            Func<List<string>> blockers, Action<string> log, Action<int> progress, CancellationToken token)
        {
            var result = new CleanupResult();
            // Revalidate membership so deletion cannot be redirected to arbitrary paths.
            var allowed = new HashSet<string>(FindCandidates(roots), StringComparer.OrdinalIgnoreCase);
            int processed = 0;
            foreach (string path in approved)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var running = blockers();
                    if (running.Count > 0)
                    {
                        log("Закройте все окна 1С\r\n" + String.Join("\r\n", running));
                        result.Stopped = true;
                        break;
                    }
                    if (!allowed.Contains(path)) throw new IOException("Каталог больше не входит в список кэша: " + path);
                    AssertNoLinks(path);
                    Preflight(path, token);
                    DeleteTree(path, token);
                    result.Deleted++;
                    log("Удалено: " + path);
                }
                catch (OperationCanceledException) { result.Stopped = true; log("Очистка остановлена пользователем. Текущий каталог мог очиститься частично."); break; }
                catch (Exception ex)
                {
                    result.Failed++;
                    log("Не очищено полностью: " + path + "\r\n" + ex.Message);
                    // A process-inspection failure must stop all subsequent deletion.
                    if (ex is ProcessInspectionException) { result.Stopped = true; break; }
                }
                progress(++processed);
            }
            return result;
        }
    }

    public sealed class ProcessInspectionException : Exception
    {
        public ProcessInspectionException(string message, Exception inner) : base(message, inner) { }
    }
}
