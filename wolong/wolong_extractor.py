"""
WoLong RDB file extractor.

Extracts files from .rdb.bin container or external .file paths.
Handles extended zlib (chunked with 10-byte headers) and raw data.
"""

import struct
import zlib
from pathlib import Path

CHUNK_SIZE = 0x4000  # 16 KB max decompressed chunk
FLAGS_EXTERNAL = 0x00010000
FLAGS_SELF_REF = 0x00410000
FLAGS_INTERNAL = 0x00420000

COMPRESSION_MASK = 0x00F00000
COMPRESSION_NONE = 0x00000000
COMPRESSION_EXTENDED = 0x00400000


def decompress_extended_zlib(data: bytes, uncompressed_size: int) -> bytes:
    """Decompress extended-zlib data with 10-byte chunk headers.

    Format: [10-byte header][zlib stream] repeated until done.
    """
    output = bytearray()
    offset = 0

    while offset < len(data) and len(output) < uncompressed_size:
        if offset + 10 > len(data):
            break
        # Read 10-byte chunk header
        chunk_zsize = struct.unpack_from("<H", data, offset)[0]
        offset += 10  # skip full header

        if chunk_zsize == 0 or chunk_zsize == 0xFFFF:
            break

        remaining = uncompressed_size - len(output)
        expected = min(remaining, CHUNK_SIZE)
        chunk_data = data[offset : offset + chunk_zsize]
        offset += chunk_zsize

        decompressed = zlib.decompress(chunk_data)
        output.extend(decompressed)

    return bytes(output[:uncompressed_size])


def read_idrk_data(f, offset: int) -> bytes | None:
    """Read and decompress data from an IDRK block at given offset."""
    f.seek(offset)
    magic = f.read(4)
    if magic != b"IDRK":
        return None

    f.read(4)  # version
    all_block_size, comp_size, uncomp_size = struct.unpack("<qqq", f.read(24))
    param_data_size, hash_name, hash_type, flags, resource_id, param_count = (
        struct.unpack("<iiiiiI", f.read(24))
    )
    f.read(param_count * 12)  # skip KRDI params
    f.read(param_data_size)   # skip param data

    raw = f.read(comp_size)
    comp_type = (flags >> 20) & 0x3F

    if comp_type == 4:  # Extended zlib
        return decompress_extended_zlib(raw, uncomp_size)
    elif comp_type == 0:  # No compression
        return raw
    else:
        print(f"  [warn] unknown compression type {comp_type}")
        return raw


def resolve_external_path(rdb_dir: str, file_ktid: int,
                          folder_path: str) -> str:
    """Build path to external .file: data/{xx}/0x{hash}.file."""
    folder_prefix = f"{file_ktid & 0xFF:02x}"
    filename = f"0x{file_ktid:08X}.file"
    return str(Path(rdb_dir) / folder_path / folder_prefix / filename)


def extract_entry(entry: dict, rdb_dir: str, output_dir: str) -> str | None:
    """Extract a single RDB entry to output_dir.

    Returns output file path on success, None on failure.
    """
    flags = entry["flags"]

    # Self-reference entries have no data
    if flags == FLAGS_SELF_REF or entry["data_size"] == 0:
        return None

    out_name = entry.get("name") or f"0x{entry['file_ktid']:08X}"
    out_path = str(Path(output_dir) / out_name)

    if flags & 0x000F0000 == FLAGS_EXTERNAL:
        # External file in data/ directory
        ext_path = resolve_external_path(
            rdb_dir, entry["file_ktid"], "data/"
        )
        if not Path(ext_path).exists():
            return None
        with open(ext_path, "rb") as ef:
            data = ef.read()
    else:
        # Internal: read from .rdb.bin
        rdb_bin = str(Path(rdb_dir) / (Path(entry["_rdb_name"]).stem + ".rdb.bin"))
        bin_offset = entry.get("bin_offset", 0)
        if bin_offset == 0:
            return None
        with open(rdb_bin, "rb") as bf:
            data = read_idrk_data(bf, bin_offset)

    if data:
        Path(output_dir).mkdir(parents=True, exist_ok=True)
        with open(out_path, "wb") as of:
            of.write(data)
        return out_path
    return None
