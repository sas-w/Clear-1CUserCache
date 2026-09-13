using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OneCCacheCleaner;

public static class EngineTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Put(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "fixture"); }
    private static List<string> Empty() { return new List<string>(); }
    private static void Ignore(string text) { }
    private static void Progress(int count) { }
    private static string NewCache(string root) { return Path.Combine(root, Guid.NewGuid().ToString()); }
    public static string Run(string temp)
    {
        string sandbox = Path.Combine(temp, "onec-exe-test-" + Guid.NewGuid());
        Directory.CreateDirectory(sandbox);
        try
        {
            var roots = new List<string>();
            foreach (string branch in new[] { "Local", "Roaming" })
            foreach (string version in new[] { "1Cv8", "1Cv82" })
            {
                string root = Path.Combine(sandbox, branch, "1C", version);
                roots.Add(root);
                string cache = NewCache(root);
                Put(Path.Combine(cache, Guid.NewGuid().ToString(), "vrs-cache", "cache.1CD"));
                string readOnly = Path.Combine(cache, "Config", "readonly.bin");
                Put(readOnly);
                File.SetAttributes(readOnly, FileAttributes.ReadOnly);
                foreach (string keep in new[] { "1CEStart", "licensing", "tmplts", "not-a-guid" }) Put(Path.Combine(root, keep, "keep.txt"));
            }
            var candidates = CacheEngine.FindCandidates(roots);
            Check(candidates.Count == 4, "Four cache roots");
            var result = CacheEngine.Clean(roots, candidates, Empty, Ignore, Progress, CancellationToken.None);
            Check(result.Deleted == 4 && result.Failed == 0, "Cache + vrs-cache + readonly deletion");
            foreach (string root in roots)
            foreach (string keep in new[] { "1CEStart", "licensing", "tmplts", "not-a-guid" })
                Check(File.Exists(Path.Combine(root, keep, "keep.txt")), "Preserve " + keep);

            foreach (string protectedPath in new[] { "licensing/license.txt", "1CEStart/ibases.v8i", "data.1CD", "license.lic", "cache.1CD", "Config/cache.1CD", "vrs-cache/1Cv8.1CD" })
            {
                string cache = NewCache(roots[0]);
                string file = Path.Combine(cache, protectedPath);
                Put(file);
                string other = Path.Combine(cache, "ordinary.bin");
                Put(other);
                result = CacheEngine.Clean(roots, new[] { cache }, Empty, Ignore, Progress, CancellationToken.None);
                Check(result.Failed == 1 && result.Deleted == 0 && File.Exists(file) && File.Exists(other), "Preflight protection: " + protectedPath);
            }

            string blocked = NewCache(roots[1]);
            string blockedFile = Path.Combine(blocked, "keep.bin");
            Put(blockedFile);
            result = CacheEngine.Clean(roots, new[] { blocked }, () => new List<string> { "1cv8.exe, PID 123" }, Ignore, Progress, CancellationToken.None);
            Check(result.Stopped && result.Deleted == 0 && File.Exists(blockedFile), "Running client blocks deletion");
            result = CacheEngine.Clean(roots, new[] { blocked }, () => { throw new ProcessInspectionException("WMI unavailable", null); }, Ignore, Progress, CancellationToken.None);
            Check(result.Stopped && File.Exists(blockedFile), "WMI failure blocks deletion");

            string second = NewCache(roots[1]);
            Put(Path.Combine(second, "keep.bin"));
            int checks = 0;
            result = CacheEngine.Clean(roots, new[] { blocked, second }, () => ++checks == 1 ? Empty() : new List<string> { "new client" }, Ignore, Progress, CancellationToken.None);
            Check(result.Deleted == 1 && result.Stopped && Directory.Exists(second), "Recheck before each cache");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                result = CacheEngine.Clean(roots, new[] { second }, Empty, Ignore, Progress, cancel.Token);
                Check(result.Stopped && Directory.Exists(second), "Cancellation preserves pending cache");
            }
            string outside = Path.Combine(sandbox, "outside", "keep.bin");
            Put(outside);
            result = CacheEngine.Clean(roots, new[] { Path.GetDirectoryName(outside) }, Empty, Ignore, Progress, CancellationToken.None);
            Check(result.Failed == 1 && File.Exists(outside), "Reject arbitrary deletion target");
            return "PASS: four roots, exclusions, vrs-cache, readonly files, preflight, running clients, WMI failure, process recheck, cancellation, target restriction.";
        }
        finally { Directory.Delete(sandbox, true); }
    }
}
