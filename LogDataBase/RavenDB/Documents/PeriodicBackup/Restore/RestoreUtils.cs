using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Raven.Client;
using Raven.Client.Documents.Smuggler;

namespace Raven.Server.Documents.PeriodicBackup.Restore
{
    public static class RestoreUtils
    {
        private static readonly Regex BackupFolderRegex = new Regex(@".ravendb-(.+)-([A-Za-z]+)-(.+)$", RegexOptions.Compiled);
        private const string LegacyIncrementalBackupExtension = "ravendb-incremental-dump";
        private const string LegacyFullBackupExtension = "ravendb-full-dump";
        
        public static void FetchRestorePoints(
            string directoryPath,
            SortedList<DateTime, RestorePoint> sortedList,
            bool assertLegacyBackups = false)
        {
            const string legacyEsentBackupFile = "RavenDB.Backup";
            const string legacyVoronBackupFile = "RavenDB.Voron.Backup";

            var files = Directory.GetFiles(directoryPath)
                .Where(filePath =>
                {
                    if (assertLegacyBackups)
                    {
                        var fileName = Path.GetFileName(filePath);
                        if (fileName.Equals(legacyEsentBackupFile, StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals(legacyVoronBackupFile, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Cannot restore a legacy backup (v3.x and below). " +
                                                                "You can restore a v3.x periodic export backup or " +
                                                                "use an export from v3.x and import it using the studio");
                        }
                    }

                    return IsBackupOrSnapshot(filePath);
                })
                .OrderBackups();


            var folderDetails = ParseFolderName(directoryPath);
            var filesCount = 0;
            var firstFile = true;
            var snapshotRestore = false;
            foreach (var filePath in files)
            {
                var extension = Path.GetExtension(filePath);
                var isSnapshot = Constants.Documents.PeriodicBackup.SnapshotExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
                if (firstFile)
                {
                    snapshotRestore = isSnapshot;
                }
                else if (isSnapshot)
                {
                    throw new InvalidOperationException($"Cannot have a snapshot backup file ({Path.GetFileName(filePath)}) after other backup files!");
                }

                firstFile = false;
                filesCount++;

                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                var fileDate = TryExtractDateFromFileName(fileNameWithoutExtension, filePath);
                while (sortedList.ContainsKey(fileDate))
                {
                    fileDate = fileDate.AddMilliseconds(1);
                }

                sortedList.Add(fileDate, new RestorePoint
                {
                    DateTime = fileDate,
                    Location = directoryPath,
                    FileName = Path.GetFileName(filePath),
                    IsSnapshotRestore = snapshotRestore,
                    IsIncremental = IsIncremetal(extension),
                    FilesToRestore = filesCount,
                    DatabaseName = folderDetails.DatabaseName,
                    NodeTag = folderDetails.NodeTag
                });
            }
        }

        public static bool IsBackupOrSnapshot(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            return
                IsBackup(filePath) ||
                Constants.Documents.PeriodicBackup.SnapshotExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBackup(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            return
                Constants.Documents.PeriodicBackup.IncrementalBackupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                Constants.Documents.PeriodicBackup.FullBackupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                LegacyIncrementalBackupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                LegacyFullBackupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime TryExtractDateFromFileName(string fileName, string filePath)
        {
            if (DateTime.TryParseExact(
                    fileName,
                    PeriodicBackupRunner.DateTimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime result) == false)
            {
                result = File.GetLastWriteTimeUtc(filePath).ToLocalTime();
            }

            return result;
        }

        private static bool IsIncremetal(string extension)
        {
            return
                Constants.Documents.PeriodicBackup.IncrementalBackupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                LegacyIncrementalBackupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
        }

        private static (string DatabaseName, string NodeTag) ParseFolderName(string directoryPath)
        {
            // [Date].ravendb-[Database Name]-[Node Tag]-[Backup Type]
            // [DATE] - format: "yyyy-MM-dd-HH-mm"
            // [Backup Type] - backup/snapshot
            // example: //2018-02-03-15-34.ravendb-Northwind-A-backup

            var lastFolderName = Path.GetFileName(directoryPath);
            var match = BackupFolderRegex.Match(lastFolderName);
            return match.Success
                ? (match.Groups[1].Value, match.Groups[2].Value)
                : (null, null);
        }

        public class DescendedDateComparer : IComparer<DateTime>
        {
            public int Compare(DateTime x, DateTime y)
            {
                return Comparer<DateTime>.Default.Compare(y, x);
            }
        }
    }
}
