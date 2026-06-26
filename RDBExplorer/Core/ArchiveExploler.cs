using RDBExplorer.Core.Formats;
using RDBExplorer.Core.Formats.ObjectDatabaseFile;
using RDBExplorer.Core.Models;
using RDBExplorer.Services;
using RDBExplorer.Utils;
using System.Security.Cryptography;
using System.Text;

namespace RDBExplorer.Core
{
    public class ArchiveExploler
    {
        private const int CHUNK_SIZE_DECOMPRESSED = 0x4000; // 16 KB

        public struct WorkerStatus
        {
            public bool IsSuccessed { get; set; }
            public string ErrorMessage { get; set; }

            public WorkerStatus(bool status)
            {
                IsSuccessed = status;
            }

            public WorkerStatus(bool status, string message)
            {
                IsSuccessed = status;
                ErrorMessage = message;
            }
        }

        public List<RDBEntry> RDBEntries { get; private set; }
        private Dictionary<uint, RDBEntry> _ktidCache = new();
        private Dictionary<uint, RDBEntry> _ktidHashNameCache = new();
        private bool _isWoLong = false;

        private string _workDir;

        public void Browse(string rdbFilePath)
        {
            if (!File.Exists(rdbFilePath))
            {
                return;
            }
            _workDir = Path.GetDirectoryName(rdbFilePath);

            // Detect format: .rdx exists -> Nioh 3, .rdb.bin exists -> WoLong
            string rdxPath = Path.Combine(_workDir,
                Path.GetFileNameWithoutExtension(rdbFilePath) + ".rdx");
            string rdbBinPath = rdbFilePath + ".bin";

            if (File.Exists(rdxPath))
            {
                _isWoLong = false;
                var rdb = new RDBReader();
                RDBEntries = rdb.Read(rdbFilePath);
            }
            else if (File.Exists(rdbBinPath))
            {
                _isWoLong = true;

                // The slow part is BuildHashNameCache scanning all .rdb.bin files.
                // Cache the parsed result so it only runs once per unchanged archive set.
                string cacheKey = ComputeCacheKey(rdbFilePath, rdbBinPath);
                string cacheFile = GetCacheFilePath(rdbFilePath);

                if (TryLoadCache(cacheFile, cacheKey))
                {
                    Console.WriteLine("[ArchiveExploler] Loaded from cache.");
                    return;
                }

                var rdb = new WoLongRDBReader();
                RDBEntries = rdb.Read(rdbFilePath);
            }
            else
            {
                throw new FileNotFoundException(
                    "Neither .rdx nor .rdb.bin found alongside the RDB file.");
            }

            _ktidCache = RDBEntries.GroupBy(e => e.FileKtid)
                .ToDictionary(g => g.Key, g => g.First());

            // WoLong: build hash_name -> entry cache from .rdb.bin IDRK blocks
            _ktidHashNameCache.Clear();
            if (_isWoLong)
            {
                BuildHashNameCache(rdbFilePath);
                SaveCache(GetCacheFilePath(rdbFilePath), ComputeCacheKey(rdbFilePath, rdbBinPath));
            }
        }

        public byte[]? GetEntryData(RDBEntry entry)
        {
            string countainerPath = Path.Combine(_workDir, entry.Location.ContainerPath);
            if (!File.Exists(countainerPath))
            {
                return null;
                //throw new FileNotFoundException($"Container not found: {countainerPath}");
            }

            using (var fsInput = new FileStream(countainerPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fsInput))
            {
                long fileOffsetInContainer = 0L;
                if (_isWoLong)
                {
                    // WoLong: offset is stored directly in Location.Offset
                    fileOffsetInContainer = (long)entry.Location.Offset;
                }
                else if (entry.Location.NewFlags == RDBFlagsNew.Internal)
                {
                    fileOffsetInContainer = (long)entry.Location.Offset;
                }

                fsInput.Position = fileOffsetInContainer;

                KRDIEntry kRDIEntry = ReadKRDIContainer(reader);

                // goto data offset
                //fsInput.Position = fileOffsetInContainer + (long)(alllBlockSize - compressedSize);
                RDBFlags flags = kRDIEntry.Header.Flags;

                uint rawFlags = (uint)kRDIEntry.Header.Flags;

                uint compressionType = (rawFlags >> 20) & 0x3F;

                bool isZlib = (compressionType == (uint)RDBFlags.CompressionZlib);
                bool isZlibExtended = (compressionType == (uint)RDBFlags.CompressionExtended);
                bool isEncrypted = (compressionType == (uint)RDBFlags.CompressionEncrypted);

                long uncompressedSize = kRDIEntry.Header.UncompressedSize;
                if (isZlib || isZlibExtended)
                {
                    // decompress logic
                    byte[] outputBuffer = new byte[uncompressedSize];
                    long currentExtractedSize = 0;

                    while (currentExtractedSize < uncompressedSize)
                    {
                        uint zSize;
                        if (isZlibExtended)
                        {
                            // custom 10 bytes header
                            zSize = reader.ReadUInt16();
                            fsInput.Seek(8, SeekOrigin.Current);
                        }
                        else
                        {
                            zSize = reader.ReadUInt32();
                        }

                        if (zSize == 0 || zSize == 0xFFFFFFFF)
                            break;

                        byte[] compressedChunk = reader.ReadBytes((int)zSize);

                        int remaining = (int)(uncompressedSize - currentExtractedSize);
                        int expectedSize = Math.Min(remaining, CHUNK_SIZE_DECOMPRESSED);

                        byte[] decompressedChunk = CompressUtils.DecompressZlibChunk(compressedChunk, expectedSize);
                        Buffer.BlockCopy(decompressedChunk, 0, outputBuffer, (int)currentExtractedSize, decompressedChunk.Length);
                        currentExtractedSize += decompressedChunk.Length;
                    }
                    return outputBuffer;
                }
                // idk what it, maybe encrypted or just raw data, try read as uncompressed size
                else if (isEncrypted)
                {
                    Console.WriteLine($"File: {entry.Name} is might be encrypted");
                    return reader.ReadBytes((int)kRDIEntry.Header.UncompressedSize);
                }

                // extract data is raw might be uncompressed
                else
                {
                    return reader.ReadBytes((int)uncompressedSize);
                }
            }
        }

        private KRDIEntry ReadKRDIContainer(BinaryReader reader)
        {
            KRDIEntry kRDIEntry = new KRDIEntry();

            KRDIHeader kRDIHeader = new KRDIHeader();
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != "IDRK")
            {
                throw new Exception($"Expected IDRK magic, got {magic}");
            }

            kRDIHeader.Magic = magic;
            kRDIHeader.Version = Encoding.ASCII.GetString(reader.ReadBytes(4));
            kRDIHeader.AllBlockSize = reader.ReadInt64();
            kRDIHeader.CompressedSize = reader.ReadInt64();
            kRDIHeader.UncompressedSize = reader.ReadInt64();
            kRDIHeader.ParamDataSize = reader.ReadInt32();
            kRDIHeader.HashName = reader.ReadInt32();
            kRDIHeader.HashType = reader.ReadInt32();
            kRDIHeader.Flags = (RDBFlags)reader.ReadUInt32();
            kRDIHeader.ResourceId = reader.ReadUInt32();
            kRDIHeader.ParamCount = reader.ReadInt32();

            kRDIEntry.Header = kRDIHeader;

            if (kRDIHeader.ParamCount > 0)
            {
                List<KRDIParam> krdiParams = new List<KRDIParam>();
                for (int i = 0; i < kRDIHeader.ParamCount; i++)
                {
                    var param = new KRDIParam();
                    param.Type = reader.ReadInt32();
                    param.Unk = reader.ReadUInt32();
                    param.HashName = reader.ReadInt32();
                    krdiParams.Add(param);
                }

                kRDIEntry.KRDIParams = krdiParams;
                kRDIEntry.ParamData = reader.ReadBytes(kRDIHeader.ParamDataSize);
            }
            return kRDIEntry;
        }

        public WorkerStatus Extract(RDBEntry entry, string outputFolder, bool withName)
        {
            try
            {
                byte[]? data = GetEntryData(entry);

                if (data == null)
                {
                    return new WorkerStatus(false, "Failed to get entry data");
                }

                string extension = TypeIDHelper.GetExtension(entry.TypeInfoKtid);
                // Synthetic entries (from hash_name cache) have TypeInfoKtid=0;
                // detect extension from data magic bytes
                if (string.IsNullOrEmpty(extension) && data.Length >= 4)
                {
                    string magic = Encoding.ASCII.GetString(data, 0, 4);
                    if (magic == "GT1G") extension = ".g1t";
                    else if (magic == "MG1G") extension = ".g1m";
                    else if (magic == "TOC\0" || magic == "COT\0") extension = ".toc";
                }

                string fileName = string.Empty;
                if (withName)
                {
                    // WoLong entries have embedded names; sanitize for filesystem
                    if (_isWoLong && !string.IsNullOrEmpty(entry.Name) && entry.TypeInfoKtid != 0)
                        fileName = entry.Name.Replace("@", "_");
                    else
                        fileName = $"0x{entry.FileKtid:X8}{extension}";
                }
                else
                {
                    fileName = $"0x{entry.FileKtid:X8}{extension}";
                }
                string outPath = Path.Combine(outputFolder, fileName);
                string directory = Path.GetDirectoryName(outPath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(outPath, data);

                Console.WriteLine($"[Done] Extracted: {fileName}");
                return new WorkerStatus(true);
            }
            catch (Exception ex)
            {
                return new WorkerStatus(false, ex.Message);
            }
        }

        public WorkerStatus InjectData(RDBEntry entry, byte[] modData, string rdbFilePath)
        {
            try
            {
                string containerPath = Path.Combine(_workDir, entry.Location.ContainerPath);
                if (!File.Exists(containerPath))
                {
                    return new WorkerStatus(false, "Container not found");
                }

                KRDIEntry originalContainer;

                using (var fsRead = new FileStream(containerPath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fsRead))
                {
                    fsRead.Position = (long)entry.Location.Offset;
                    originalContainer = ReadKRDIContainer(reader);
                }

                byte[] fullModdedBlock = CreateModifiedIDRK(originalContainer, modData);

                long newOffset;
                using (var fsAppend = new FileStream(containerPath, FileMode.Append, FileAccess.Write))
                {
                    long padding = (16 - (fsAppend.Position % 16)) % 16;
                    for (int i = 0; i < padding; i++)
                    {
                        fsAppend.WriteByte(0);
                    }

                    newOffset = fsAppend.Position;
                    fsAppend.Write(fullModdedBlock, 0, fullModdedBlock.Length);
                }

                entry.Location.Offset = (ulong)newOffset;
                entry.Location.SizeInContainer = (uint)fullModdedBlock.Length;
                entry.FileSize = modData.Length;

                UpdateRDBFile(rdbFilePath, entry);

                Console.WriteLine($"[Success] Injected 0x{entry.FileKtid:X8} with original metadata.");
                // update rdb enrty in gloal list

                return new WorkerStatus(true);
            }
            catch (Exception ex)
            {
                return new WorkerStatus(false, ex.Message);
            }
        }

        private byte[] CreateModifiedIDRK(KRDIEntry original, byte[] modData)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                uint rawFlags = (uint)original.Header.Flags;
                uint noCompressionFlags = rawFlags & ~(0x3Fu << 20);

                int paramsSize = (original.KRDIParams?.Count ?? 0) * 12;
                long totalBlockSize = 56 + paramsSize + original.Header.ParamDataSize + modData.Length;

                writer.Write(Encoding.ASCII.GetBytes("IDRK"));
                writer.Write(Encoding.ASCII.GetBytes(original.Header.Version));
                writer.Write(totalBlockSize);
                writer.Write((long)modData.Length);
                writer.Write((long)modData.Length);
                writer.Write(original.Header.ParamDataSize);
                writer.Write(original.Header.HashName);
                writer.Write(original.Header.HashType);
                writer.Write(noCompressionFlags);
                writer.Write(original.Header.ResourceId);
                writer.Write(original.Header.ParamCount);

                if (original.Header.ParamCount > 0 && original.KRDIParams != null)
                {
                    foreach (var p in original.KRDIParams)
                    {
                        writer.Write(p.Type);
                        writer.Write(p.Unk);
                        writer.Write(p.HashName);
                    }
                    writer.Write(original.ParamData);
                }

                writer.Write(modData);

                return ms.ToArray();
            }
        }

        private void UpdateRDBFile(string rdbPath, RDBEntry entry)
        {
            using (var fsRdb = new FileStream(rdbPath, FileMode.Open, FileAccess.ReadWrite))
            using (var writer = new BinaryWriter(fsRdb))
            {
                // goto file entry position
                long fileSizePos = entry.EntryOffsetInRDB + 24;
                fsRdb.Position = fileSizePos;
                writer.Write((long)entry.FileSize);

                // goto medata location
                int metadataOffset = 48 + (entry.UnkContent?.Length ?? 0);
                fsRdb.Position = entry.EntryOffsetInRDB + metadataOffset;

                // write new flags
                writer.Write((ushort)entry.Location.NewFlags);

                if (entry.DataSize == 0x11) // 64-bit offset format
                {
                    byte high = (byte)((entry.Location.Offset >> 32) & 0xFF);
                    uint low = (uint)(entry.Location.Offset & 0xFFFFFFFF);

                    writer.Write(high);
                    fsRdb.Seek(3, SeekOrigin.Current);
                    writer.Write(low);
                }
                else // 0x0D - 32-bit offset format
                {
                    writer.Write((uint)entry.Location.Offset);
                }
                writer.Write((uint)entry.Location.SizeInContainer);
            }
        }

        private void BuildHashNameCache(string rdbFilePath)
        {
            string? dir = Path.GetDirectoryName(rdbFilePath);
            if (string.IsNullOrEmpty(dir)) return;

            var offsetMap = RDBEntries
                .Where(e => e.Location.Offset > 0 && !string.IsNullOrEmpty(e.Location.ContainerPath))
                .GroupBy(e => (long)e.Location.Offset)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var binFile in Directory.GetFiles(dir, "*.rdb.bin"))
            {
                string binName = Path.GetFileName(binFile);
                int fileCount = 0;

                using (var fs = new FileStream(binFile, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    if (fs.Length < 16) continue;
                    fs.Seek(16, SeekOrigin.Begin);

                    while (fs.Position < fs.Length)
                    {
                        long aligned = (fs.Position + 15) & ~15L;
                        if (aligned > fs.Position)
                            fs.Seek(aligned, SeekOrigin.Begin);

                        long blockStart = fs.Position;
                        if (blockStart + 56 > fs.Length) break;

                        string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                        if (magic != "IDRK") break;

                        reader.ReadBytes(4); // version
                        long allBlockSize = reader.ReadInt64();
                        reader.ReadBytes(8 + 8); // compressedSize, uncompressedSize
                        int paramDataSize = reader.ReadInt32();
                        uint hashNameU = (uint)reader.ReadInt32();
                        int hashType = reader.ReadInt32();
                        reader.ReadBytes(4 + 4); // flags, resourceId
                        int paramCount = reader.ReadInt32();

                        if (hashNameU != 0 && !_ktidHashNameCache.ContainsKey(hashNameU))
                        {
                            if (offsetMap.TryGetValue(blockStart, out var existing))
                            {
                                _ktidHashNameCache[hashNameU] = existing;
                            }
                            else
                            {
                                _ktidHashNameCache[hashNameU] = new RDBEntry
                                {
                                    FileKtid = hashNameU,
                                    Location = new EntryLocation
                                    {
                                        ContainerPath = binName,
                                        Offset = (ulong)blockStart,
                                        NewFlags = RDBFlagsNew.Internal,
                                    }
                                };
                            }
                            fileCount++;
                        }

                        // Skip params + paramData + compressed data
                        int paramsSize = paramCount * 12;
                        reader.ReadBytes(paramsSize + paramDataSize);
                        long dataRemaining = allBlockSize - (fs.Position - blockStart);
                        if (dataRemaining > 0)
                            reader.ReadBytes((int)dataRemaining);
                        long nextPos = (fs.Position + 15) & ~15L;
                        fs.Seek(nextPos, SeekOrigin.Begin);
                    }
                }
                Console.WriteLine($"[BuildHashNameCache] {binName}: added {fileCount} (total: {_ktidHashNameCache.Count})");
            }
        }

        public RDBEntry? FindEntryByKtId(uint ktid)
        {
            if (_ktidCache.TryGetValue(ktid, out var entry))
            {
                return entry;
            }
            // WoLong fallback: lookup by IDRK block hash_name
            if (_ktidHashNameCache.TryGetValue(ktid, out entry))
            {
                return entry;
            }
            return null;
        }

        public static string MakeName(RDBEntry entry)
        {
            bool withName = SettingsService.Instance.Config.ExportWithNames;
            string fileName = string.Empty;
            if (withName)
            {
                fileName = entry.Name ?? $"0x{entry.FileKtid:X8}{TypeIDHelper.GetExtension(entry.TypeInfoKtid)}";
            }
            else
            {
                fileName = $"0x{entry.FileKtid:X8}{TypeIDHelper.GetExtension(entry.TypeInfoKtid)}";
            }
            return fileName;
        }

        #region Cache

        private const int CACHE_VERSION = 1;
        private const string CACHE_MAGIC = "RDBC";

        private string GetCacheDirectory()
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RDBExplorer", "cache");
            Directory.CreateDirectory(cacheDir);
            return cacheDir;
        }

        private string GetCacheFilePath(string rdbFilePath)
        {
            string cacheDir = GetCacheDirectory();
            string pathHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rdbFilePath))));
            return Path.Combine(cacheDir, $"{pathHash}.rdbcache");
        }

        private string ComputeCacheKey(string rdbFilePath, string rdbBinPath)
        {
            var sb = new StringBuilder();
            AppendFileInfo(sb, rdbFilePath);
            AppendFileInfo(sb, rdbBinPath);

            foreach (string binFile in Directory.GetFiles(_workDir, "*.rdb.bin")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                AppendFileInfo(sb, binFile);
            }

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        private static void AppendFileInfo(StringBuilder sb, string path)
        {
            var fi = new FileInfo(path);
            sb.Append(fi.FullName).Append(':')
              .Append(fi.LastWriteTimeUtc.Ticks).Append(':')
              .Append(fi.Length).Append(';');
        }

        private void SaveCache(string cacheFilePath, string cacheKey)
        {
            try
            {
                using var fs = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(fs);

                writer.Write(Encoding.ASCII.GetBytes(CACHE_MAGIC));
                writer.Write(CACHE_VERSION);
                writer.Write(_isWoLong ? 1 : 0);

                byte[] keyBytes = Encoding.UTF8.GetBytes(cacheKey);
                writer.Write(keyBytes.Length);
                writer.Write(keyBytes);

                writer.Write(RDBEntries.Count);
                foreach (var entry in RDBEntries)
                {
                    WriteEntry(writer, entry);
                }

                writer.Write(_ktidHashNameCache.Count);
                foreach (var kvp in _ktidHashNameCache)
                {
                    writer.Write(kvp.Key);
                    WriteEntry(writer, kvp.Value);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ArchiveExploler] Failed to save cache: {ex.Message}");
                try { File.Delete(cacheFilePath); } catch { }
            }
        }

        private bool TryLoadCache(string cacheFilePath, string cacheKey)
        {
            try
            {
                if (!File.Exists(cacheFilePath))
                    return false;

                using var fs = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (magic != CACHE_MAGIC)
                    return false;

                int version = reader.ReadInt32();
                if (version != CACHE_VERSION)
                    return false;

                bool isWoLong = reader.ReadInt32() != 0;

                int keyLength = reader.ReadInt32();
                string storedKey = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
                if (storedKey != cacheKey)
                    return false;

                _isWoLong = isWoLong;

                int entryCount = reader.ReadInt32();
                RDBEntries = new List<RDBEntry>(entryCount);
                for (int i = 0; i < entryCount; i++)
                {
                    RDBEntries.Add(ReadEntry(reader));
                }

                _ktidCache = RDBEntries.GroupBy(e => e.FileKtid)
                    .ToDictionary(g => g.Key, g => g.First());

                // Rebuild hash-name cache while preserving references to RDBEntries where possible.
                int hashCacheCount = reader.ReadInt32();
                _ktidHashNameCache = new Dictionary<uint, RDBEntry>(hashCacheCount);
                var offsetMap = RDBEntries
                    .Where(e => e.Location.Offset > 0 && !string.IsNullOrEmpty(e.Location.ContainerPath))
                    .GroupBy(e => (long)e.Location.Offset)
                    .ToDictionary(g => g.Key, g => g.First());

                for (int i = 0; i < hashCacheCount; i++)
                {
                    uint hashName = reader.ReadUInt32();
                    var cachedEntry = ReadEntry(reader);

                    if (offsetMap.TryGetValue((long)cachedEntry.Location.Offset, out var existing))
                    {
                        _ktidHashNameCache[hashName] = existing;
                    }
                    else
                    {
                        _ktidHashNameCache[hashName] = cachedEntry;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ArchiveExploler] Failed to load cache: {ex.Message}");
                return false;
            }
        }

        private static void WriteEntry(BinaryWriter writer, RDBEntry entry)
        {
            WriteString(writer, entry.Magic);
            writer.Write(entry.Version);
            writer.Write(entry.EntrySize);
            writer.Write(entry.DataSize);
            writer.Write(entry.FileSize);
            writer.Write(entry.EntryType);
            writer.Write(entry.FileKtid);
            writer.Write(entry.TypeInfoKtid);
            writer.Write((uint)entry.Flags);

            byte[] unk = entry.UnkContent ?? Array.Empty<byte>();
            writer.Write(unk.Length);
            if (unk.Length > 0)
                writer.Write(unk);

            writer.Write(entry.EntryOffsetInRDB);
            writer.Write((uint)entry.Location.NewFlags);
            WriteString(writer, entry.Location.ContainerPath);
            writer.Write(entry.Location.Offset);
            writer.Write(entry.Location.SizeInContainer);
            writer.Write(entry.Location.FDataId);
        }

        private static RDBEntry ReadEntry(BinaryReader reader)
        {
            return new RDBEntry
            {
                Magic = ReadString(reader),
                Version = reader.ReadUInt32(),
                EntrySize = reader.ReadInt64(),
                DataSize = reader.ReadInt64(),
                FileSize = reader.ReadInt64(),
                EntryType = reader.ReadUInt32(),
                FileKtid = reader.ReadUInt32(),
                TypeInfoKtid = reader.ReadUInt32(),
                Flags = (RDBFlags)reader.ReadUInt32(),
                UnkContent = reader.ReadBytes(reader.ReadInt32()),
                EntryOffsetInRDB = reader.ReadInt64(),
                Location = new EntryLocation
                {
                    NewFlags = (RDBFlagsNew)reader.ReadUInt32(),
                    ContainerPath = ReadString(reader),
                    Offset = reader.ReadUInt64(),
                    SizeInContainer = reader.ReadUInt64(),
                    FDataId = reader.ReadInt32()
                }
            };
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length <= 0)
                return string.Empty;
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        #endregion
    }
}
