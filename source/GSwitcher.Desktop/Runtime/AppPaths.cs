using System;
using System.IO;

namespace GSwitcher.Services
{
    public static class AppPaths
    {
        private static readonly string RunId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N");
        private static readonly string RootDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "GSwitcher",
            RunId);

        public static string RuntimeRootDirectory
        {
            get
            {
                Directory.CreateDirectory(RootDirectoryPath);
                return RootDirectoryPath;
            }
        }

        public static string WebView2UserDataDirectory
        {
            get
            {
                var directory = Path.Combine(RuntimeRootDirectory, "WebView2");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        public static string StateFilePath
        {
            get { return Path.Combine(RuntimeRootDirectory, "state.json"); }
        }

        public static string LogsDirectory
        {
            get
            {
                var directory = Path.Combine(RuntimeRootDirectory, "logs");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        public static string LogFilePath
        {
            get { return Path.Combine(LogsDirectory, "gswitcher.log"); }
        }

        public static string ControlDirectory
        {
            get
            {
                var directory = Path.Combine(RuntimeRootDirectory, "control");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }
    }
}
