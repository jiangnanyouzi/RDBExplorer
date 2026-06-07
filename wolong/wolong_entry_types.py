"""
WoLong RDB entry allParams / dependency parser.

allParams structure: 8 + depCount * 12 + trailingSize bytes.
The first 8 bytes are (uint32 padding, uint32 depCount).
Each dependency is 12 bytes: (uint32 index, uint32 type, uint32 ktid).
Trailing bytes contain type-specific parameters.
"""

import struct
from io import BytesIO

# EntryType → (depCount, trailingSize)
ENTRY_TYPE_MAP = {
    0x00: (0, 0),    # Simple entry, no deps
    0x01: (1, 1),    # Single dep, 1 trailing byte
    0x04: (1, 4),    # Single dep, 4 trailing bytes
    0x08: (2, 8),    # Two deps, 8 trailing bytes
    0x10: (4, 16),   # Four deps, 16 trailing bytes
}

DEPENDENCY_SIZE = 12  # bytes per dependency entry
BASE_PARAMS_SIZE = 8  # padding(4) + depCount(4)


def expected_all_params_size(entry_type: int) -> int:
    """Calculate expected allParams size for a given EntryType."""
    if entry_type not in ENTRY_TYPE_MAP:
        return -1  # Unknown type
    dep_count, trailing = ENTRY_TYPE_MAP[entry_type]
    return BASE_PARAMS_SIZE + dep_count * DEPENDENCY_SIZE + trailing


def parse_all_params(raw: bytes, entry_type: int) -> dict:
    """Parse allParams blob into structured data.

    Returns dict with 'padding', 'dep_count', 'dependencies', 'trailing'.
    """
    buf = BytesIO(raw)

    # Read base fields
    padding = struct.unpack("<I", buf.read(4))[0]
    dep_count = struct.unpack("<I", buf.read(4))[0]

    # Read dependencies
    dependencies = []
    for _ in range(dep_count):
        dep_index, dep_type, dep_ktid = struct.unpack("<III", buf.read(12))
        dependencies.append({
            "index": dep_index, "type": dep_type, "ktid": dep_ktid,
        })

    # Remaining bytes are trailing data
    trailing = buf.read()

    return {
        "padding": padding,
        "dep_count": dep_count,
        "dependencies": dependencies,
        "trailing": trailing,
    }


def classify_entry_type(all_params_size: int) -> str:
    """Match allParamsSize to known EntryType pattern."""
    for etype, (deps, trail) in ENTRY_TYPE_MAP.items():
        if all_params_size == BASE_PARAMS_SIZE + deps * DEPENDENCY_SIZE + trail:
            return f"0x{etype:02X} ({deps} deps, {trail} trailing)"
    return f"unknown (size={all_params_size})"
