using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TrRebootTools.Shared.Cdc.Avengers;
using TrRebootTools.Shared.Cdc.Rise;
using TrRebootTools.Shared.Cdc.Shadow;
using TrRebootTools.Shared.Cdc.Tr2013;
using TrRebootTools.Shared.Util;

namespace TrRebootTools.Shared.Cdc
{
    public abstract class Archive : IDisposable
    {
        protected const int Magic = 0x53464154;

        private const int MaxResourceChunkSize = 0x40000;

        // The game engine addresses content within a single .tiger part using 32-bit offsets, and the official
        // archives never go beyond 2 GB per part. When the last part reaches the soft limit, further content is
        // written to a new part (.001.tiger, .002.tiger, ...). The gap between the soft and hard limit leaves room
        // for the largest single resource to finish writing. The part number is stored in 4 bits, so max 16 parts.
        private const long SoftPartSizeLimit = 0x70000000;
        private const long HardPartSizeLimit = 0x7FFFF800;

        private int _numParts = 1;
        private List<Stream>? _partStreams;
        private readonly List<ArchiveFileDescriptor> _files = [];
        private long _nextFileDescPos;
        private int _maxFiles;
        private bool _hasWrittenResources;

        protected Archive(string baseFilePath, ArchiveMetaData? metaData)
        {
            BaseFilePath = baseFilePath;
            MetaData = metaData;
        }

        protected abstract CdcGame Game { get; }
        protected abstract int HeaderVersion { get; }
        protected abstract bool SupportsSubId { get; }
        protected abstract bool SupportsLanguageList { get; }
        protected abstract ArchiveFileDescriptor ReadFileDescriptor(BinaryReader reader);
        protected abstract void WriteFileDescriptor(BinaryWriter writer, ArchiveFileDescriptor file);
        protected virtual int ContentAlignment => 0x10;

        public static Archive Create(string baseFilePath, int id, int subId, ArchiveMetaData? metaData, int maxFiles, CdcGame game)
        {
            Archive archive = InstantiateArchive(baseFilePath, metaData, game);
            archive.Id = id;
            archive.SubId = subId;
            archive._maxFiles = maxFiles;

            archive.DeleteExtraPartFiles();

            Stream stream = archive.OpenPart(baseFilePath, 0, FileMode.Create, FileAccess.ReadWrite);
            archive._partStreams = [stream];

            BinaryWriter writer = new(stream);
            ArchiveHeader header =
                new()
                {
                    Magic = Magic,
                    Version = archive.HeaderVersion,
                    NumParts = 1,
                    Id = archive.Id
                };
            writer.WriteStruct(header);

            if (archive.SupportsSubId)
                writer.Write(archive.SubId);

            string platformStr = CdcGameInfo.Get(game).ArchivePlatform;
            byte[] platformBytes = new byte[0x20];
            Encoding.ASCII.GetBytes(platformStr, 0, platformStr.Length, platformBytes, 0);
            writer.Write(platformBytes);

            archive._nextFileDescPos = stream.Position;
            ArchiveFileDescriptor file = new ArchiveFileDescriptor(0, 0, 0, 0, 0, 0, 0);
            for (int i = 0; i < maxFiles; i++)
            {
                archive.WriteFileDescriptor(writer, file);
            }

            return archive;
        }

        public static Archive Open(string baseFilePath, ArchiveMetaData? metaData, CdcGame game)
        {
            Archive archive = InstantiateArchive(baseFilePath, metaData, game);

            using Stream stream = archive.OpenPart(baseFilePath, 0, FileMode.Open, FileAccess.ReadWrite);
            BinaryReader reader = new(stream);
            
            ArchiveHeader header = reader.ReadStruct<ArchiveHeader>();
            if (header.Magic != Magic)
                throw new InvalidDataException("Invalid magic in tiger file");

            if (header.Version != archive.HeaderVersion)
                throw new NotSupportedException($"Only version {archive.HeaderVersion} archive files are supported");

            archive._numParts = header.NumParts;
            archive._maxFiles = header.NumFiles;
            archive.Id = header.Id;

            if (archive.SupportsSubId)
            {
                archive.SubId = reader.ReadInt32();
            }
            else
            {
                string archiveName = Path.GetFileName(baseFilePath).Replace(".000.tiger", "");
                archive.SubId = CdcGameInfo.Get(game).Languages.IndexOf(l => archiveName.EndsWith(l.Name, StringComparison.InvariantCultureIgnoreCase)) + 1;
            }

            reader.Skip(0x20);

            if (archive.SupportsLanguageList)
            {
                int languageBits = reader.ReadInt32();
                int numLanguages = reader.ReadInt32();
                reader.Skip(numLanguages * 0x18);
            }

            for (int i = 0; i < header.NumFiles; i++)
            {
                archive._files.Add(archive.ReadFileDescriptor(reader));
            }

            return archive;
        }

        internal static Archive InstantiateArchive(string baseFilePath, ArchiveMetaData? metaData, CdcGame game)
        {
            return game switch
            {
                CdcGame.Tr2013      => new Tr2013Archive(baseFilePath, metaData),
                CdcGame.Rise        => new RiseArchive(baseFilePath, metaData),
                CdcGame.Shadow      => new ShadowArchive(baseFilePath, metaData),
                CdcGame.Avengers    => new AvengersArchive(baseFilePath, metaData),
                _ => throw new NotSupportedException()
            };
        }

        private List<Stream> PartStreams
        {
            get
            {
                if (_partStreams == null)
                {
                    lock (this)
                    {
                        if (_partStreams == null)
                        {
                            _partStreams = [];
                            for (int i = 0; i < _numParts; i++)
                            {
                                string partFilePath = GetPartFilePath(i);
                                _partStreams.Add(OpenPart(partFilePath, i, FileMode.Open, ModName != null ? FileAccess.ReadWrite : FileAccess.Read));
                            }
                        }
                    }
                }
                return _partStreams;
            }
        }

        internal virtual Stream OpenPart(string filePath, int partIdx, FileMode mode, FileAccess access)
        {
            return File.Open(filePath, mode, access, FileShare.ReadWrite);
        }

        public int NumParts => _numParts;

        public string BaseFilePath
        {
            get;
        }

        public int Id
        {
            get;
            private set;
        }

        public int SubId
        {
            get;
            private set;
        }

        public ArchiveMetaData? MetaData
        {
            get;
        }

        public string? ModName
        {
            get
            {
                string? entry = MetaData?.CustomEntries.FirstOrDefault(c => c.StartsWith("mod:"));
                return entry?.Substring("mod:".Length);
            }
        }

        public IReadOnlyCollection<ArchiveFileDescriptor> Files => _files;

        public ResourceCollection? GetResourceCollection(ArchiveFileDescriptor file)
        {
            if (file.ArchiveId != Id || file.ArchiveSubId != SubId)
                throw new ArgumentException("File reference does not match archive", nameof(file));

            string? filePath = CdcHash.Lookup(file.NameHash, Game, true);
            if (filePath == null || Path.GetExtension(filePath) != ".drm")
                return null;

            using Stream stream = OpenFile(file);
            try
            {
                return ResourceCollection.Open(file.NameHash, file.Locale, stream, Game);
            }
            catch
            {
                return null;
            }
        }

        // True if the data at the given position starts with the "CDRM" marker that every resource blob begins with
        public bool HasResourceHeaderAt(int part, long offset)
        {
            List<Stream> partStreams = PartStreams;
            if (part < 0 || part >= partStreams.Count)
                return false;

            Stream stream = partStreams[part];
            if (offset < 0 || offset + 4 > stream.Length)
                return false;

            long savedPosition = stream.Position;
            try
            {
                stream.Position = offset;
                byte[] marker = new byte[4];
                return stream.Read(marker, 0, 4) == 4 && BitConverter.ToUInt32(marker, 0) == 0x4D524443;
            }
            finally
            {
                stream.Position = savedPosition;
            }
        }

        public string DescribeResourceCollectionFailure(ArchiveFileDescriptor file)
        {
            string? filePath = CdcHash.Lookup(file.NameHash, Game, true);
            if (filePath == null)
                return $"the name for file hash 0x{file.NameHash:X} was not found in the game's .list file. Make sure the .list file for this game (e.g. TR2013_PC_Release.list) is in {AppContext.BaseDirectory}";

            if (Path.GetExtension(filePath) != ".drm")
                return $"{filePath} is not a .drm file";

            try
            {
                using Stream stream = OpenFile(file);
                ResourceCollection.Open(file.NameHash, file.Locale, stream, Game);
                return $"{filePath} opened fine on retry";
            }
            catch (Exception ex)
            {
                return $"{filePath} could not be read or parsed ({ex.GetType().Name}: {ex.Message})";
            }
        }

        public Stream OpenFile(ArchiveFileDescriptor file)
        {
            if (file.ArchiveId != Id || file.ArchiveSubId != SubId)
                throw new ArgumentException("File reference does not match archive", nameof(file));

            Stream partStream = PartStreams[file.ArchivePart];
            if (ArchiveDecompressionStream.IsCompressed(partStream, file))
                return new ArchiveDecompressionStream(partStream, file, true);
            else
                return new WindowedStream(partStream, file.Offset, file.Length);
        }

        public Stream OpenResource(ResourceDescriptor resource)
        {
            if (resource.ArchiveId != Id || resource.ArchiveSubId != SubId)
                throw new ArgumentException("Resource reference does not match archive", nameof(resource));

            Stream stream = PartStreams[resource.ArchivePart];
            if (resource.RefDefinitionsSize + resource.BodySize == resource.Length)
                return new WindowedStream(stream, resource.Offset, resource.Length);

            return new ArchiveDecompressionStream(stream, resource, true);
        }

        public ArchiveFileDescriptor AddFile(ArchiveFileKey identifier, byte[] data)
        {
            return AddFile(identifier.NameHash, identifier.Locale, data);
        }

        public ArchiveFileDescriptor AddFile(ArchiveFileKey identifier, ArraySegment<byte> data)
        {
            return AddFile(identifier.NameHash, identifier.Locale, data);
        }

        public ArchiveFileDescriptor AddFile(ulong nameHash, ulong locale, byte[] data)
        {
            return AddFile(nameHash, locale, new ArraySegment<byte>(data));
        }

        public ArchiveFileDescriptor AddFile(ulong nameHash, ulong locale, ArraySegment<byte> data)
        {
            if (_files.Count == _maxFiles)
                throw new InvalidOperationException("Can't add any further files");

            if (data.Array == null)
                throw new ArgumentNullException("No file data specified", nameof(data));

            int archivePart = PrepareWritablePart();
            Stream contentStream = PartStreams[archivePart];
            BinaryWriter contentWriter = new BinaryWriter(contentStream);
            contentStream.Position = contentStream.Length;
            contentWriter.Align(ContentAlignment);

            uint offset = (uint)contentStream.Length;
            contentWriter.Write(data.Array, data.Offset, data.Count);

            Stream indexStream = PartStreams[0];
            BinaryWriter indexWriter = new BinaryWriter(indexStream);

            ArchiveFileDescriptor file = new(nameHash, locale, Id, SubId, archivePart, offset, (uint)data.Count);
            indexStream.Position = _nextFileDescPos;
            WriteFileDescriptor(indexWriter, file);
            _nextFileDescPos = indexStream.Position;

            _files.Add(file);
            indexStream.Position = 0xC;
            indexWriter.Write(_files.Count);

            return file;
        }

        public ArchiveBlobDescriptor AddResource(Stream contentStream)
        {
            int archivePart = PrepareWritablePart();
            Stream partStream = PartStreams[archivePart];
            partStream.Position = partStream.Length;

            BinaryWriter writer = new(partStream);

            uint resourceOffset = ((uint)partStream.Position + (uint)ContentAlignment - 1) & ~((uint)ContentAlignment - 1);

            if (_hasWrittenResources)
            {
                uint nextMarkerOffset = (uint)partStream.Position - 0x10;
                partStream.Position -= 0xC;
                writer.Write(resourceOffset - nextMarkerOffset);
                partStream.Position += 8;
            }

            writer.Align(ContentAlignment);

            WriteResource(contentStream, writer);
            uint resourceLength = (uint)partStream.Position - resourceOffset;

            writer.Align(0x10);
            writer.Write(0x5458454E);       // "NEXT" marker
            writer.Write(0);                // Offset to next resource (0 for last)
            writer.Align(0x10);
            _hasWrittenResources = true;

            if (partStream.Length > HardPartSizeLimit)
                throw new InvalidOperationException($"A single resource pushed archive part {archivePart} past the 2 GB limit; this resource is too large to install");

            return new ArchiveBlobDescriptor(Id, SubId, archivePart, resourceOffset, resourceLength);
        }

        private void WriteResource(Stream contentStream, BinaryWriter writer)
        {
            if (contentStream is ArchiveDecompressionStream resourceStream)
            {
                Stream archivePartStream = resourceStream.ArchivePartStream;
                ArchiveBlobDescriptor resourceBlob = resourceStream.BlobDescriptor;
                archivePartStream.CopySegmentTo(resourceBlob.Offset, resourceBlob.Length, writer.BaseStream);
                return;
            }

            Stream partStream = writer.BaseStream;

            int numChunks = (int)(contentStream.Length / MaxResourceChunkSize);
            if (contentStream.Length % MaxResourceChunkSize != 0)
                numChunks++;

            writer.Write(0x4D524443);      // Magic
            writer.Write(0);               // Type
            writer.Write(numChunks);
            writer.Write(0);

            long chunkSizesOffset = partStream.Position;
            for (int i = 0; i < numChunks; i++)
            {
                writer.Write(0);
                writer.Write(0);
            }
            writer.Align(0x10);

            long remainingSize = contentStream.Length;
            byte[] uncompressedChunkBuffer = new byte[MaxResourceChunkSize];
            for (int i = 0; i < numChunks; i++)
            {
                Span<byte> uncompressedChunkData = uncompressedChunkBuffer[0..Math.Min((int)remainingSize, MaxResourceChunkSize)];
                contentStream.Read(uncompressedChunkData);
                WriteResourceChunk(partStream, writer, uncompressedChunkData, ref chunkSizesOffset);
                remainingSize -= uncompressedChunkData.Length;
            }
        }

        private void WriteResourceChunk(Stream partStream, BinaryWriter writer, Span<byte> uncompressedData, ref long chunkSizesPos)
        {
            long chunkPos = partStream.Position;
            writer.Write((byte)0x78);
            writer.Write((byte)0x9C);
            using (DeflateStream compressor = new(partStream, CompressionMode.Compress, true))
            {
                compressor.Write(uncompressedData);
            }
            writer.Write(0);
            int compressedSize = (int)(partStream.Position - chunkPos);
            byte compressionType = 0x02;

            if (compressedSize > MaxResourceChunkSize)
            {
                partStream.Position = chunkPos;
                partStream.Write(uncompressedData);
                partStream.SetLength(partStream.Position);
                compressedSize = uncompressedData.Length;
                compressionType = 0x01;
            }

            partStream.Position = chunkSizesPos;
            writer.Write(uncompressedData.Length << 8 | compressionType);
            writer.Write(compressedSize);
            chunkSizesPos += 8;

            partStream.Position = partStream.Length;
            writer.Align(0x10);
        }

        private int PrepareWritablePart()
        {
            List<Stream> partStreams = PartStreams;
            int lastPart = partStreams.Count - 1;
            if (partStreams[lastPart].Length < SoftPartSizeLimit)
                return lastPart;

            int maxParts = CdcGameInfo.Get(Game).MaxArchiveParts;
            if (partStreams.Count >= maxParts)
                throw new InvalidOperationException($"The archive would need more than {maxParts} parts (about {maxParts * (SoftPartSizeLimit >> 20) / 1024} GB), which this game's archive format can't address");

            int newPart = partStreams.Count;
            partStreams.Add(OpenPart(GetPartFilePath(newPart), newPart, FileMode.Create, FileAccess.ReadWrite));
            _numParts = partStreams.Count;
            _hasWrittenResources = false;       // The "NEXT" marker chain restarts in every part

            Stream indexStream = partStreams[0];
            indexStream.Position = 8;           // ArchiveHeader.NumParts
            new BinaryWriter(indexStream).Write(_numParts);
            return newPart;
        }

        public void DeleteExtraPartFiles()
        {
            int maxParts = CdcGameInfo.Get(Game).MaxArchiveParts;
            for (int i = 1; i < maxParts; i++)
            {
                string partFilePath = GetPartFilePath(i);
                if (File.Exists(partFilePath))
                    File.Delete(partFilePath);
            }
        }

        public string GetPartFilePath(int part)
        {
            return BaseFilePath.Replace(".000.tiger", $".{part:d03}.tiger");
        }

        public void CloseStreams()
        {
            if (_partStreams == null)
                return;

            foreach (Stream stream in _partStreams)
            {
                stream.Dispose();
            }
            _partStreams = null;
        }

        public void Delete()
        {
            if (SubId == 0)
            {
                if (MetaData != null)
                    File.Delete(MetaData.FilePath);

                File.Delete(SpecMasksToc.GetFilePathForArchive(BaseFilePath));
            }

            Dispose();
            for (int i = 0; i < _numParts; i++)
            {
                File.Delete(GetPartFilePath(i));
            }
        }

        public override string ToString()
        {
            return Path.GetFileName(BaseFilePath);
        }

        public void Dispose()
        {
            CloseStreams();
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ArchiveHeader
        {
            public int Magic;
            public int Version;
            public int NumParts;
            public int NumFiles;
            public int Id;
        }
    }
}
