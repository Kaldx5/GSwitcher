using System;
using System.IO;
using System.Threading;

namespace GSwitcher.Services
{
    public static class RuntimeCleanupService
    {
        public static void CleanupStaleRuntimeRoots()
        {
            try
            {
                var parent = Path.Combine(Path.GetTempPath(), "GSwitcher");
                if (!Directory.Exists(parent))
                {
                    return;
                }

                foreach (var directory in Directory.GetDirectories(parent))
                {
                    try
                    {
                        var info = new DirectoryInfo(directory);
                        if (string.Equals(info.FullName, AppPaths.RuntimeRootDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        info.Delete(true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public static void CleanupCurrentRuntimeRoot()
        {
            var root = AppPaths.RuntimeRootDirectory;

            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }

                    return;
                }
                catch
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(250);
                }
            }
        }
    }
}
