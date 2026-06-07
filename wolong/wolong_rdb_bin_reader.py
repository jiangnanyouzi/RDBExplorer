"""
WoLong Fallen Dynasty .rdb.bin (PDRK container) parser.

Parses PDRK header and iterates sequential IDRK data blocks.
Blocks are 16-byte aligned with zero padding between them.
"""

import struct
from typing import BinaryIO, Generator


def align16(pos: int) -> int:
    """Round position up to next 16-byte boundary."""
    return (pos + 15) & ~15


def parse_pdrk_header(reader: BinaryIO) -> dict:
    """Parse 16-byte PDRK container header."""
    magic = reader.read(4).decode("ascii")
    if magic != "PDRK":
        raise ValueError(f"Invalid PDRK magic: {magic!r}")

    version = reader.read(4)  # raw bytes (often ASCII "0000")
    size1, size2 = struct.unpack("<II", reader.read(8))
    return {"magic": magic, "version": version, "size1": size1, "size2": size2}


def parse_idrk_block_header(reader: BinaryIO) -> dict:
    """Parse 56-byte IDRK block header (KRDIHeader equivalent).

    Returns header dict with file position of the data payload.
    """
    block_start = reader.tell()
    magic = reader.read(4).decode("ascii")
    if magic != "IDRK":
        raise ValueError(f"Expected IDRK at 0x{block_start:X}, got {magic!r}")

    version = reader.read(4)
    all_block_size, compressed_size, uncompressed_size = struct.unpack(
        "<qqq", reader.read(24)
    )
    param_data_size, hash_name, hash_type, flags, resource_id, param_count = (
        struct.unpack("<iiiiiI", reader.read(24))
    )

    # Read KRDIParams (12 bytes each) + ParamData
    params = []
    for _ in range(param_count):
        p_type, p_unk, p_hash = struct.unpack("<III", reader.read(12))
        params.append({"type": p_type, "unk": p_unk, "hash": p_hash})

    if param_data_size > 0:
        reader.read(param_data_size)

    data_offset = reader.tell()
    data_size = all_block_size - (data_offset - block_start)

    return {
        "magic": magic, "version": version,
        "all_block_size": all_block_size,
        "compressed_size": compressed_size,
        "uncompressed_size": uncompressed_size,
        "param_data_size": param_data_size,
        "hash_name": hash_name, "hash_type": hash_type,
        "flags": flags, "resource_id": resource_id,
        "param_count": param_count, "params": params,
        "block_start": block_start, "data_offset": data_offset,
        "data_size": data_size,
    }


def iterate_blocks(filepath: str) -> Generator[dict, None, None]:
    """Yield all IDRK blocks from a .rdb.bin file."""
    with open(filepath, "rb") as f:
        header = parse_pdrk_header(f)
        yield {"pdrk_header": header}

        while True:
            # Align to 16-byte boundary
            pos = f.tell()
            aligned = align16(pos)
            if aligned > pos:
                f.read(aligned - pos)

            pos = f.tell()
            peek = f.read(4)
            if len(peek) < 4:
                break
            f.seek(pos)

            block = parse_idrk_block_header(f)
            # Skip past the data payload
            f.seek(block["data_offset"] + block["data_size"])
            yield block


if __name__ == "__main__":
    import sys
    path = sys.argv[1] if len(sys.argv) > 1 else (
        r"E:\SteamLibrary\steamapps\common\WoLongFallenDynasty"
        r"\motor_package\system.rdb.bin"
    )
    count = 0
    for block in iterate_blocks(path):
        if "pdrk_header" in block:
            h = block["pdrk_header"]
            print(f"PDRK header: size1={h['size1']}, size2={h['size2']}")
            continue
        if count < 5:
            print(f"  block @{block['block_start']:08X}: "
                  f"hash=0x{block['hash_name'] & 0xFFFFFFFF:08X} "
                  f"type=0x{block['hash_type'] & 0xFFFFFFFF:08X} "
                  f"flags=0x{block['flags']:08X} "
                  f" uncomp={block['uncompressed_size']}")
        count += 1
    print(f"  ... total {count} blocks")
