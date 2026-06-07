"""
WoLong Fallen Dynasty RDB file parser.

Parses .rdb files containing IDRK entry metadata.
Compatible with KTGL engine RDB format (WoLong variant).
"""

import struct
from pathlib import Path
from typing import BinaryIO

RDB_HEADER_SIZE = 32
RDB_ENTRY_HEADER_SIZE = 48


def read_ascii(reader: BinaryIO, length: int) -> str:
    """Read fixed-length ASCII string, strip null bytes."""
    return reader.read(length).decode("ascii").rstrip("\x00")


def parse_rdb_header(reader: BinaryIO) -> dict:
    """Parse 32-byte _DRK RDB file header."""
    magic = read_ascii(reader, 4)
    if magic != "_DRK":
        raise ValueError(f"Invalid RDB magic: {magic!r}, expected '_DRK'")

    version = reader.read(4)  # raw bytes (often ASCII "0000")
    header_size, system_id, file_count = struct.unpack("<III", reader.read(12))
    database_id = struct.unpack("<I", reader.read(4))[0]
    folder_path = read_ascii(reader, 8)

    return {
        "magic": magic,
        "version": version,
        "header_size": header_size,
        "system_id": system_id,
        "file_count": file_count,
        "database_id": database_id,
        "folder_path": folder_path,
    }


def _align4(pos: int) -> int:
    """Round position up to next 4-byte boundary."""
    return (pos + 3) & ~3


def parse_name_string(reader: BinaryIO, data_size: int) -> dict:
    """Parse the metadata string from DataSize bytes.

    WoLong stores 'hexoffset@hexsize' as ASCII in the DataSize field,
    e.g. '3a66af50@12924' means offset=0x3a66af50, size=0x12924.
    """
    if data_size <= 0:
        return {"name": "", "bin_offset": 0, "bin_size": 0}

    raw = reader.read(data_size)
    text = raw.decode("ascii").rstrip("\x00")

    if "@" not in text:
        return {"name": text, "bin_offset": 0, "bin_size": 0}

    offset_hex, size_hex = text.split("@", 1)
    return {
        "name": text,
        "bin_offset": int(offset_hex, 16),
        "bin_size": int(size_hex, 16),
    }


def parse_rdb_entry(reader: BinaryIO) -> dict | None:
    """Parse a single 48-byte IDRK entry header + allParams + name."""
    pos = reader.tell()
    aligned = _align4(pos)
    if aligned > pos:
        reader.read(aligned - pos)

    magic = read_ascii(reader, 4)
    if magic != "IDRK":
        raise ValueError(f"Invalid entry magic at 0x{pos:X}: {magic!r}")

    (version_raw,) = struct.unpack("<I", reader.read(4))
    entry_size, data_size, file_size = struct.unpack("<qqq", reader.read(24))
    entry_type, file_ktid, type_ktid, flags = struct.unpack("<IIII", reader.read(16))

    all_params_size = entry_size - data_size - RDB_ENTRY_HEADER_SIZE
    all_params = reader.read(all_params_size) if all_params_size > 0 else b""

    name_info = parse_name_string(reader, data_size)

    return {
        "magic": magic, "version_raw": version_raw,
        "entry_size": entry_size, "data_size": data_size,
        "file_size": file_size, "entry_type": entry_type,
        "file_ktid": file_ktid, "type_ktid": type_ktid, "flags": flags,
        "all_params": all_params, **name_info,
    }


def parse_all_rdb_entries(filepath: str) -> tuple[dict, list[dict]]:
    """Parse entire .rdb file. Returns (header, entries)."""
    with open(filepath, "rb") as f:
        header = parse_rdb_header(f)
        entries = []
        for _ in range(header["file_count"]):
            entry = parse_rdb_entry(f)
            if entry is not None:
                entries.append(entry)
        return header, entries


if __name__ == "__main__":
    import sys
    path = sys.argv[1] if len(sys.argv) > 1 else (
        r"E:\SteamLibrary\steamapps\common\WoLongFallenDynasty"
        r"\motor_package\system.rdb"
    )
    header, entries = parse_all_rdb_entries(path)
    print(f"Magic: {header['magic']}, FileCount: {header['file_count']}")
    print(f"FolderPath: {header['folder_path']!r}")
    for e in entries[:5]:
        print(f"  {e['name']:<24} ktid=0x{e['file_ktid']:08X} "
              f"type=0x{e['type_ktid']:08X} flags=0x{e['flags']:08X} "
              f"size={e['file_size']}")
    print(f"  ... total {len(entries)} entries")
