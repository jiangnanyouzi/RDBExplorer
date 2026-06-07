using RDBExplorer.Core.Models;
using System.Text;

namespace RDBExplorer.Core.Formats
{
    public class WoLongRDBReader
    {
        private const int RDB_ENTRY_HEADER_SIZE = 48;
        private RDBHeader _RDBHeader;
        private string _rdbBinName;

        public List<RDBEntry> Read(string filePath)
        {
            string workDir = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            _rdbBinName = $"{fileName}.rdb.bin";

            var entries = new List<RDBEntry>();

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                ReadHeader(reader);

                for (int i = 0; i < _RDBHeader.FileCount; i++)
                {
                    // align by 4 byte
                    while (fs.Position % 4 != 0)
                        fs.ReadByte();

                    long entryStartPos = fs.Position;
                    var entry = ReadEntry(reader, entryStartPos);
                    if (entry != null)
                        entries.Add(entry);
                }
            }
            return entries;
        }

        private void ReadHeader(BinaryReader reader)
        {
            var headerMagic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (headerMagic != "_DRK")
                throw new Exception("Not a valid RDB file");

            _RDBHeader = new RDBHeader
            {
                Magic = headerMagic,
                Version = reader.ReadInt32(),
                HeaderSize = reader.ReadInt32(),
                SystemId = reader.ReadInt32(),
                FileCount = reader.ReadInt32(),
                DatabaseId = reader.ReadUInt32(),
                FolderPath = Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0')
            };
        }

        private RDBEntry ReadEntry(BinaryReader reader, long entryStartPos)
        {
            var entry = new RDBEntry
            {
                EntryOffsetInRDB = entryStartPos,
                Magic = Encoding.ASCII.GetString(reader.ReadBytes(4)),
                Version = reader.ReadUInt32(),
                EntrySize = reader.ReadInt64(),
                DataSize = reader.ReadInt64(),
                FileSize = reader.ReadInt64(),
                EntryType = reader.ReadUInt32(),
                FileKtid = reader.ReadUInt32(),
                TypeInfoKtid = reader.ReadUInt32(),
                Flags = (RDBFlags)reader.ReadUInt32()
            };

            // Read allParams (between header and name string)
            int allParamsSize = (int)entry.EntrySize - (int)entry.DataSize - RDB_ENTRY_HEADER_SIZE;
            if (allParamsSize > 0)
                entry.UnkContent = reader.ReadBytes(allParamsSize);

            // WoLong: DataSize = length of ASCII name string "hexoffset@hexsize"
            ParseNameAndLocation(reader, entry);

            return entry;
        }

        private void ParseNameAndLocation(BinaryReader reader, RDBEntry entry)
        {
            int metadataSize = (int)entry.DataSize;

            if (metadataSize <= 0)
            {
                // Self-reference entry (Flags=0x00410000), no data
                entry.Location.ContainerPath = "";
                return;
            }

            // Read ASCII name string, e.g. "3a66af50@12924"
            byte[] nameBytes = reader.ReadBytes(metadataSize);
            string nameStr = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

            // Parse "hexoffset@hexsize" format
            int atIdx = nameStr.IndexOf('@');
            if (atIdx > 0)
            {
                string offsetHex = nameStr.Substring(0, atIdx);
                entry.Location.Offset = Convert.ToUInt64(offsetHex, 16);
                entry.Location.ContainerPath = _rdbBinName;
            }

            // Set name directly (WoLong embeds names in entries)
            entry.Name = nameStr;

            // Determine storage flags from entry Flags field
            uint flags = (uint)entry.Flags;
            uint storage = flags & 0x000F0000;

            if (storage == 0x00010000) // External
            {
                entry.Location.NewFlags = RDBFlagsNew.External;
                string folderPrefix = (entry.FileKtid & 0xFF).ToString("X2").ToLower();
                entry.Location.ContainerPath =
                    $"{_RDBHeader.FolderPath}{folderPrefix}/0x{entry.FileKtid:X8}.file";
            }
            else if (storage == 0x00020000 || storage == 0x00000000)
            {
                // Internal or default (most entries with 0x00420000)
                entry.Location.NewFlags = RDBFlagsNew.Internal;
            }
        }
    }
}
