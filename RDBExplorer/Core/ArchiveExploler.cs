using RDBExplorer.Core.Formats;
using RDBExplorer.Core.Formats.ObjectDatabaseFile;
using RDBExplorer.Core.Models;
using RDBExplorer.Services;
using RDBExplorer.Utils;
using System.Diagnostics;
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

            string? cacheKey = null;
            string? cacheFile = null;

            if (File.Exists(rdxPath))
            {
                _isWoLong = false;
                var rdb = new RDBReader();
                RDBEntries = rdb.Read(rdbFilePath);
            }
            else if (File.Exists(rdbBinPath))
            {
                _isWoLong = true;

                // Indexing the .rdb.bin set walks every IDRK block header, so keep the parsed
                // result on disk and only rebuild it when the archive set actually changes.
                cacheKey = ComputeCacheKey(rdbFilePath, rdbBinPath);
                cacheFile = GetCacheFilePath(rdbFilePath);

                if (TryLoadCache(cacheFile, cacheKey))
                {
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
            if (_isWoLong && cacheFile != null && cacheKey != null)
            {
                BuildHashNameCache(rdbFilePath);
                SaveCache(cacheFile, cacheKey);
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

            var totalWatch = Stopwatch.StartNew();

            // Blocks are addressed by (container, offset): the same offset exists in several
            // .rdb.bin files (every file starts at 16), so keying by offset alone can hand back
            // an entry that points into the wrong archive.
            var offsetMap = new Dictionary<(string Container, long Offset), RDBEntry>(RDBEntries.Count);
            foreach (var entry in RDBEntries)
            {
                if (entry.Location == null || entry.Location.Offset == 0 ||
                    string.IsNullOrEmpty(entry.Location.ContainerPath))
                {
                    continue;
                }

                offsetMap.TryAdd((entry.Location.ContainerPath, (long)entry.Location.Offset), entry);
            }

            string[] binFiles = Directory.GetFiles(dir, "*.rdb.bin")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Header-only scan of every block: the payload is skipped, so this reads a few MB
            // instead of the full archive set (43 GB for a retail WoLong install).
            // The files are independent, so index them in parallel.
            var scanned = new List<WoLongRdbBinScanner.Block>[binFiles.Length];
            Parallel.For(0, binFiles.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(binFiles.Length, Environment.ProcessorCount)) },
                i =>
                {
                    try
                    {
                        scanned[i] = WoLongRdbBinScanner.Scan(binFiles[i]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BuildHashNameCache] {Path.GetFileName(binFiles[i])}: {ex.Message}");
                        scanned[i] = new List<WoLongRdbBinScanner.Block>();
                    }
                });

            long scanMs = totalWatch.ElapsedMilliseconds;

            int totalBlocks = 0;
            foreach (var blocks in scanned)
                totalBlocks += blocks?.Count ?? 0;

            _ktidHashNameCache = new Dictionary<uint, RDBEntry>(totalBlocks);

            for (int i = 0; i < binFiles.Length; i++)
            {
                string binName = Path.GetFileName(binFiles[i]);
                var blocks = scanned[i];
                int fileCount = 0;

                if (blocks != null)
                {
                    foreach (var block in blocks)
                    {
                        if (_ktidHashNameCache.ContainsKey(block.HashName))
                            continue;

                        if (offsetMap.TryGetValue((binName, block.Offset), out var existing))
                        {
                            _ktidHashNameCache[block.HashName] = existing;
                        }
                        else
                        {
                            _ktidHashNameCache[block.HashName] = new RDBEntry
                            {
                                FileKtid = block.HashName,
                                Location = new EntryLocation
                                {
                                    ContainerPath = binName,
                                    Offset = (ulong)block.Offset,
                                    NewFlags = RDBFlagsNew.Internal,
                                }
                            };
                        }
                        fileCount++;
                    }
                }

                Console.WriteLine($"[BuildHashNameCache] {binName}: added {fileCount} (total: {_ktidHashNameCache.Count})");
            }

            Console.WriteLine(
                $"[BuildHashNameCache] indexed {totalBlocks} blocks in {scanMs} ms (+{totalWatch.ElapsedMilliseconds - scanMs} ms merging)");
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

        private const int CACHE_VERSION = 2;
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
            var watch = Stopwatch.StartNew();
            try
            {
                // Container paths repeat for every entry ("FieldEditor4.rdb.bin"), so store them
                // once in a string table instead of once per record.
                var pathIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                var paths = new List<string>();

                void CollectPath(RDBEntry? entry)
                {
                    string? path = entry?.Location?.ContainerPath;
                    if (string.IsNullOrEmpty(path) || pathIndex.ContainsKey(path))
                        return;
                    pathIndex[path] = paths.Count;
                    paths.Add(path);
                }

                foreach (var entry in RDBEntries)
                    CollectPath(entry);
                foreach (var kvp in _ktidHashNameCache)
                    CollectPath(kvp.Value);

                using var fs = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1 << 20, FileOptions.SequentialScan);
                using var writer = new BinaryWriter(fs, Encoding.UTF8);

                writer.Write(Encoding.ASCII.GetBytes(CACHE_MAGIC));
                writer.Write(CACHE_VERSION);
                writer.Write(_isWoLong ? 1 : 0);

                byte[] keyBytes = Encoding.UTF8.GetBytes(cacheKey);
                writer.Write(keyBytes.Length);
                writer.Write(keyBytes);

                writer.Write(paths.Count);
                foreach (var path in paths)
                    WriteString(writer, path);

                writer.Write(RDBEntries.Count);
                foreach (var entry in RDBEntries)
                    WriteEntry(writer, entry, pathIndex);

                writer.Write(_ktidHashNameCache.Count);
                foreach (var kvp in _ktidHashNameCache)
                {
                    writer.Write(kvp.Key);
                    WriteEntry(writer, kvp.Value, pathIndex);
                }
                writer.Flush();

                Console.WriteLine(
                    $"[ArchiveExploler] Cache saved ({RDBEntries.Count} entries, {_ktidHashNameCache.Count} hash names, " +
                    $"{fs.Length / 1024 / 1024} MB) in {watch.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ArchiveExploler] Failed to save cache: {ex.Message}");
                try { File.Delete(cacheFilePath); } catch { }
            }
        }

        private bool TryLoadCache(string cacheFilePath, string cacheKey)
        {
            if (!File.Exists(cacheFilePath))
                return false;

            var watch = Stopwatch.StartNew();
            try
            {
                using var fs = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                using var reader = new BinaryReader(fs, Encoding.UTF8);

                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != CACHE_MAGIC)
                    return false;

                if (reader.ReadInt32() != CACHE_VERSION)
                    return false;

                bool isWoLong = reader.ReadInt32() != 0;

                int keyLength = reader.ReadInt32();
                if (keyLength < 0 || keyLength > 4096)
                    return false;

                string storedKey = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
                if (!string.Equals(storedKey, cacheKey, StringComparison.Ordinal))
                    return false;

                int pathCount = reader.ReadInt32();
                if (pathCount < 0)
                    return false;

                var paths = new string[pathCount];
                for (int i = 0; i < pathCount; i++)
                    paths[i] = ReadString(reader);

                int entryCount = reader.ReadInt32();
                if (entryCount < 0)
                    return false;

                var entries = new List<RDBEntry>(entryCount);
                for (int i = 0; i < entryCount; i++)
                    entries.Add(ReadEntry(reader, paths));

                var ktidCache = new Dictionary<uint, RDBEntry>(entryCount);
                foreach (var entry in entries)
                    ktidCache.TryAdd(entry.FileKtid, entry);

                var offsetMap = new Dictionary<(string Container, long Offset), RDBEntry>(entryCount);
                foreach (var entry in entries)
                {
                    if (entry.Location != null && entry.Location.Offset != 0 &&
                        !string.IsNullOrEmpty(entry.Location.ContainerPath))
                    {
                        offsetMap.TryAdd((entry.Location.ContainerPath, (long)entry.Location.Offset), entry);
                    }
                }

                int hashCacheCount = reader.ReadInt32();
                if (hashCacheCount < 0)
                    return false;

                // Restore hash-name lookups, re-pointing them at the RDB entry that owns the
                // same block so callers get the fully populated record.
                var hashCache = new Dictionary<uint, RDBEntry>(hashCacheCount);
                for (int i = 0; i < hashCacheCount; i++)
                {
                    uint hashName = reader.ReadUInt32();
                    var cachedEntry = ReadEntry(reader, paths);

                    hashCache[hashName] =
                        cachedEntry.Location != null &&
                        offsetMap.TryGetValue((cachedEntry.Location.ContainerPath, (long)cachedEntry.Location.Offset), out var existing)
                            ? existing
                            : cachedEntry;
                }

                // Commit only once the whole file has been read back successfully.
                _isWoLong = isWoLong;
                RDBEntries = entries;
                _ktidCache = ktidCache;
                _ktidHashNameCache = hashCache;

                Console.WriteLine(
                    $"[ArchiveExploler] Loaded cache ({entryCount} entries, {hashCacheCount} hash names) " +
                    $"in {watch.ElapsedMilliseconds} ms.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ArchiveExploler] Failed to load cache: {ex.Message}");
                return false;
            }
        }

        private static void WriteEntry(BinaryWriter writer, RDBEntry entry, Dictionary<string, int> pathIndex)
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

            string? container = entry.Location?.ContainerPath;
            int pathId = -1;
            if (!string.IsNullOrEmpty(container) && pathIndex.TryGetValue(container, out int found))
                pathId = found;

            writer.Write((uint)(entry.Location?.NewFlags ?? RDBFlagsNew.Internal));
            writer.Write(pathId);
            writer.Write(entry.Location?.Offset ?? 0UL);
            writer.Write(entry.Location?.SizeInContainer ?? 0UL);
            writer.Write(entry.Location?.FDataId ?? 0);
        }

        private static RDBEntry ReadEntry(BinaryReader reader, string[] paths)
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
                    ContainerPath = ReadPath(reader, paths),
                    Offset = reader.ReadUInt64(),
                    SizeInContainer = reader.ReadUInt64(),
                    FDataId = reader.ReadInt32()
                }
            };
        }

        private static string ReadPath(BinaryReader reader, string[] paths)
        {
            int index = reader.ReadInt32();
            return index >= 0 && index < paths.Length ? paths[index] : string.Empty;
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
