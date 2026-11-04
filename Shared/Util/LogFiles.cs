using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TrRebootTools.Shared.Util
{
    // The reports and the error log live in a "logs" folder next to the program (or, if that folder can't be written to,
    // in the user's local application data folder).
    //  - One report is written at a time. It is named after the moment it was created. When the program starts, the newest
    //    report is reused unless it is over 5 MB, in which case a new one is started. It may grow beyond that while the program is open.
    //  - When the program starts, the folder is trimmed to under 100 MB by deleting the oldest reports. The newest report is always kept.
    //  - The error log is a separate file.
    public static class LogFiles
    {
        private const string ReportPrefix = "TrRebootTools_report_";
        private const string ReportExtension = ".txt";
        private const string ErrorLogFileName = "TrRebootTools_error.log";

        private const long MaxReportSizeAtLaunch = 5L * 1024 * 1024;
        private const long MaxFolderSize = 100L * 1024 * 1024;
        private const long MaxErrorLogSize = 2L * 1024 * 1024;
        private const long ErrorLogSizeAfterTrim = 1L * 1024 * 1024;

        private static readonly object _lock = new();
        private static bool _initialized;
        private static string? _folder;
        private static string? _reportPath;
        private static string _version = "";
        private static string? _currentGame;
        private static bool _sessionStarted;

        // Called once when the program starts: picks the report to write to and tidies the logs folder
        public static void Initialize(string version)
        {
            lock (_lock)
            {
                if (_initialized)
                    return;

                _initialized = true;
                _version = version;

                try
                {
                    _folder = FindWritableFolder();
                    if (_folder == null)
                        return;

                    TrimErrorLog(_folder);

                    List<FileInfo> reports = GetReports(_folder);
                    FileInfo? newestReport = reports.LastOrDefault();
                    if (newestReport != null && newestReport.Length <= MaxReportSizeAtLaunch)
                        _reportPath = newestReport.FullName;
                    else
                        _reportPath = Path.Combine(_folder, $"{ReportPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ReportExtension}");

                    PruneFolder(_folder, reports, newestReport, _reportPath);
                }
                catch
                {
                    // Logging must never stop the program from working
                }
            }
        }

        // Called when a game is opened (and again if the user switches game). Records which version and game the following entries belong to.
        public static void StartSession(string gameName)
        {
            lock (_lock)
            {
                if (_reportPath == null || gameName == _currentGame)
                    return;

                string description = _sessionStarted ? "Switched to" : "Session started with";
                _sessionStarted = true;
                _currentGame = gameName;
                WriteToReport($"=== {description} {gameName} | {DateTime.Now:yyyy-MM-dd HH:mm:ss} | mod manager {_version} ==={Environment.NewLine}{Environment.NewLine}");
            }
        }

        public static void AppendReport(string text)
        {
            lock (_lock)
            {
                WriteToReport(text);
            }
        }

        // Adds one timestamped line to the report, for recording what the manager is doing (started / finished / failed)
        public static void LogEvent(string text)
        {
            lock (_lock)
            {
                WriteToReport($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
        }

        // Adds to the error log and returns its path (null if it couldn't be written)
        public static string? AppendError(string text)
        {
            lock (_lock)
            {
                try
                {
                    _folder ??= FindWritableFolder();
                    if (_folder == null)
                        return null;

                    string path = Path.Combine(_folder, ErrorLogFileName);
                    File.AppendAllText(path, text);
                    return path;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static void WriteToReport(string text)
        {
            if (_reportPath == null)
                return;

            try
            {
                StringBuilder sb = new();
                if (!File.Exists(_reportPath))
                {
                    sb.AppendLine($"Mod manager version: {_version}");
                    sb.AppendLine($"Game: {_currentGame ?? "unknown"}");
                    sb.AppendLine($"Report started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine();
                }
                sb.Append(text);
                File.AppendAllText(_reportPath, sb.ToString());
            }
            catch
            {
            }
        }

        private static string? FindWritableFolder()
        {
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrRebootTools", "logs")
            ];

            foreach (string candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    string probePath = Path.Combine(candidate, ".write-test");
                    using (new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                    {
                    }
                    return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        // The reports, oldest first (their names start with the date and time they were created)
        private static List<FileInfo> GetReports(string folder)
        {
            return new DirectoryInfo(folder)
                   .EnumerateFiles(ReportPrefix + "*" + ReportExtension)
                   .OrderBy(f => f.Name, StringComparer.Ordinal)
                   .ToList();
        }

        // Deletes the oldest reports until the folder is under the limit. The newest existing report and the one being written are kept.
        private static void PruneFolder(string folder, List<FileInfo> reports, FileInfo? newestReport, string currentReportPath)
        {
            long totalSize = new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length);
            foreach (FileInfo report in reports)
            {
                if (totalSize <= MaxFolderSize)
                    break;

                if (report.FullName == newestReport?.FullName || report.FullName == currentReportPath)
                    continue;

                try
                {
                    long length = report.Length;
                    report.Delete();
                    totalSize -= length;
                }
                catch
                {
                }
            }
        }

        // Keeps only the end of the error log if it has grown too large
        private static void TrimErrorLog(string folder)
        {
            string path = Path.Combine(folder, ErrorLogFileName);
            FileInfo file = new(path);
            if (!file.Exists || file.Length <= MaxErrorLogSize)
                return;

            byte[] tail = new byte[ErrorLogSizeAfterTrim];
            using (FileStream stream = file.OpenRead())
            {
                stream.Seek(-ErrorLogSizeAfterTrim, SeekOrigin.End);
                stream.ReadExactly(tail);
            }

            int start = Array.IndexOf(tail, (byte)'\n') + 1;
            File.WriteAllBytes(path, tail[start..]);
        }
    }
}
