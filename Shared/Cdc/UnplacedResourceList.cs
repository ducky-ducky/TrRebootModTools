using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TrRebootTools.Shared.Cdc
{
    public static class UnplacedResourceList
    {
        private const string FileName = "trreboottools\\modmanager\\unplaced_resources.drm";

        public static bool IsSupported(CdcGame game)
        {
            return game is CdcGame.Tr2013 or CdcGame.Rise or CdcGame.Shadow;
        }

        public static ulong GetNameHash(CdcGame game)
        {
            return CdcHash.Calculate(FileName, game);
        }

        public static bool IsListFile(ArchiveFileDescriptor file, CdcGame game)
        {
            return IsSupported(game) && file.NameHash == GetNameHash(game);
        }

        public static ResourceCollection Create(CdcGame game)
        {
            return ResourceCollection.CreateEmpty(GetNameHash(game), 0xFFFFFFFFFFFFFFFF, game);
        }

        public static ResourceCollection? Open(Archive archive, ArchiveFileDescriptor file, CdcGame game)
        {
            try
            {
                using Stream stream = archive.OpenFile(file);
                return ResourceCollection.Open(file.NameHash, file.Locale, stream, game);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        public static List<ResourceDescriptor> Read(Archive archive, ArchiveFileDescriptor file, CdcGame game)
        {
            ResourceCollection? list = Open(archive, file, game);
            if (list == null)
                return [];

            return list.Resources.Where(r => r.ArchiveId == archive.Id && r.Enabled).ToList();
        }
    }
}
