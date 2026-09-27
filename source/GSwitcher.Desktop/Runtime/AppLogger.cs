using System;
using System.IO;

namespace GSwitcher.Services
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new object();

        public static string LogFilePath
        {
            get { return AppPaths.LogFilePath; }
        }

        public static void Write(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    var line = string.Format("[{0:u}] {1}{2}", DateTime.UtcNow, message, Environment.NewLine);
                    File.AppendAllText(LogFilePath, line);
                }
            }
            catch
            {
            }
        }
    }
}
