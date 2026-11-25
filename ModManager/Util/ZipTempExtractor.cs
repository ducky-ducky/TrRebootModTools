using System;
using System.IO;
using System.Linq;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using TrRebootTools.Shared;

namespace TrRebootTools.ModManager.Util
{
    internal class ZipTempExtractor : IDisposable
    {
        private readonly string _archiveFilePath;

        public ZipTempExtractor(string archiveFilePath)
        {
            _archiveFilePath = archiveFilePath;

            FolderPath = Path.GetTempFileName();
            File.Delete(FolderPath);
            Directory.CreateDirectory(FolderPath);
        }

        public string FolderPath
        {
            get;
        }

        public void Extract(ITaskProgress progress, CancellationToken cancellationToken)
        {
            try
            {
                progress.Begin("Extracting archive...");

                using IArchive archive = ArchiveFactory.OpenArchive(_archiveFilePath);
                int numFiles = archive.Entries.Count(e => !e.IsDirectory);

                ExtractionOptions options = new ExtractionOptions { ExtractFullPath = true, Overwrite = false, PreserveFileTime = false };
                int i = 0;
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    using IReader reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory)
                            continue;

                        cancellationToken.ThrowIfCancellationRequested();

                        reader.WriteEntryToDirectory(FolderPath, options);
                        i++;
                        progress.Report((float)i / numFiles);
                    }
                }
                else
                {
                    foreach (IArchiveEntry entry in archive.Entries)
                    {
                        if (entry.IsDirectory)
                            continue;

                        cancellationToken.ThrowIfCancellationRequested();

                        entry.WriteToDirectory(FolderPath, options);
                        i++;
                        progress.Report((float)i / numFiles);
                    }
                }
            }
            finally
            {
                progress.End();
            }
        }

        public void Dispose()
        {
            Directory.Delete(FolderPath, true);
        }
    }
}
