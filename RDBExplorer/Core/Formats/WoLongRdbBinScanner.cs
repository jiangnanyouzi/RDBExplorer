using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace RDBExplorer.Core.Formats
{
    /// <summary>
    /// Header-only scanner for WoLong <c>*.rdb.bin</c> archives.
    /// </summary>
    /// <remarks>
    /// Every resource in a .rdb.bin is stored as a 16-byte aligned <c>IDRK</c> block:
    /// <code>
    /// 0x00 magic "IDRK"     0x04 version        0x08 allBlockSize (header+params+data)
    /// 0x10 compressedSize   0x18 uncompressed   0x20 paramDataSize
    /// 0x24 hashName (uint)  0x28 hashType       0x2C flags
    /// 0x30 resourceId       0x34 paramCount
    /// </code>
    /// <c>allBlockSize</c> gives the offset of the next block, so the whole 43 GB set can be
    /// indexed by reading only the 56-byte headers (~18 MB) instead of every payload.
    /// </remarks>
    public static class WoLongRdbBinScanner
    {
        /// <summary>Size of the fixed IDRK block header.</summary>
        public const int BlockHeaderSize = 56;

        private const int FirstBlockOffset = 16;
        private const int AllBlockSizeOffset = 8;
        private const int HashNameOffset = 0x24;

        private static readonly byte[] MagicIdrk = { (byte)'I', (byte)'D', (byte)'R', (byte)'K' };

        /// <summary>A single IDRK block: its start offset inside the file and its hash_name.</summary>
        public readonly struct Block
        {
            public Block(long offset, uint hashName)
            {
                Offset = offset;
                HashName = hashName;
            }

            /// <summary>Absolute offset of the block inside the .rdb.bin file.</summary>
            public long Offset { get; }

            /// <summary>Resource hash_name stored in the block header.</summary>
            public uint HashName { get; }
        }

        /// <summary>
        /// Walks the IDRK block chain of <paramref name="path"/> and returns every block in
        /// ascending offset order. Only block headers are read; payloads are skipped with seeks.
        /// </summary>
        public static List<Block> Scan(string path)
        {
            var blocks = new List<Block>();
            if (!File.Exists(path))
                return blocks;

            using SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

            long length = RandomAccess.GetLength(handle);
            if (length <= FirstBlockOffset)
                return blocks;

            Span<byte> header = stackalloc byte[BlockHeaderSize];
            long position = FirstBlockOffset;

            while (true)
            {
                long blockStart = (position + 15) & ~15L;
                if (blockStart + BlockHeaderSize > length)
                    break;

                if (RandomAccess.Read(handle, header, blockStart) < BlockHeaderSize)
                    break;

                if (header[0] != MagicIdrk[0] || header[1] != MagicIdrk[1] ||
                    header[2] != MagicIdrk[2] || header[3] != MagicIdrk[3])
                    break;

                long allBlockSize = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(AllBlockSizeOffset));
                uint hashName = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HashNameOffset));

                if (hashName != 0)
                    blocks.Add(new Block(blockStart, hashName));

                // allBlockSize covers the whole block; a non-positive size means a corrupt
                // chain, and the next block must always move forward.
                if (allBlockSize <= 0)
                    break;

                long next = blockStart + allBlockSize;
                if (next <= position)
                    break;

                position = next;
            }

            return blocks;
        }
    }
}
