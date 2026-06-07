"""
WoLong RDB analysis & verification script.

Parses all WoLong .rdb files, prints statistics,
and validates offset@size references against .rdb.bin blocks.
"""

import sys
from collections import Counter
from pathlib import Path

from wolong_rdb_reader import parse_all_rdb_entries
from wolong_entry_types import classify_entry_type, parse_all_params

WOLONG_DIR = (
    r"E:\SteamLibrary\steamapps\common"
    r"\WoLongFallenDynasty\motor_package"
)


def print_stats(label: str, counter: Counter, limit: int = 10):
    """Print top-N items from a Counter."""
    print(f"\n  {label}:")
    for val, cnt in counter.most_common(limit):
        print(f"    {val}: {cnt}")
    if len(counter) > limit:
        print(f"    ... ({len(counter)} unique total)")


def verify_offsets(rdb_path: str, entries: list[dict], sample: int = 20):
    """Spot-check that offset@size points to IDRK blocks in .rdb.bin."""
    bin_path = str(Path(rdb_path).with_suffix(".rdb.bin"))
    if not Path(bin_path).exists():
        print(f"  [skip] {bin_path} not found")
        return

    checked = ok = 0
    internal = [e for e in entries if e["bin_offset"] > 0]
    step = max(1, len(internal) // sample)

    with open(bin_path, "rb") as f:
        for i in range(0, len(internal), step):
            if checked >= sample:
                break
            entry = internal[i]
            f.seek(entry["bin_offset"])
            magic = f.read(4)
            if magic == b"IDRK":
                ok += 1
            else:
                print(f"  [FAIL] offset 0x{entry['bin_offset']:X} "
                      f"magic={magic!r}")
            checked += 1

    print(f"  Offset verification: {ok}/{checked} valid IDRK blocks")


def analyze_rdb(rdb_path: str):
    """Analyze a single RDB file and print statistics."""
    print(f"\n{'='*60}")
    print(f"File: {Path(rdb_path).name}")

    header, entries = parse_all_rdb_entries(rdb_path)
    print(f"Header: magic={header['magic']}, "
          f"file_count={header['file_count']}, "
          f"folder={header['folder_path']!r}")

    flags_ctr = Counter(f"0x{e['flags']:08X}" for e in entries)
    type_ctr = Counter(f"0x{e['type_ktid']:08X}" for e in entries)
    etype_ctr = Counter(classify_entry_type(
        e["entry_size"] - e["data_size"] - 48
    ) for e in entries)

    print(f"Total entries: {len(entries)}")
    print_stats("Flags distribution", flags_ctr)
    print_stats("TypeInfoKtid distribution", type_ctr, 8)
    print_stats("EntryType (allParams)", etype_ctr)

    # Check non-zero allParams samples
    for e in entries:
        params_size = e["entry_size"] - e["data_size"] - 48
        if params_size > 8 and len(e["all_params"]) > 8:
            parsed = parse_all_params(e["all_params"], e["entry_type"])
            if parsed["dep_count"] > 0:
                print(f"\n  Sample with deps: {e['name']}")
                print(f"    {parsed}")
                break

    verify_offsets(rdb_path, entries)


if __name__ == "__main__":
    wolong_dir = sys.argv[1] if len(sys.argv) > 1 else WOLONG_DIR
    rdb_files = sorted(Path(wolong_dir).glob("*.rdb"))
    if not rdb_files:
        print(f"No .rdb files found in {wolong_dir}")
        sys.exit(1)

    print(f"Found {len(rdb_files)} RDB files in {wolong_dir}")
    for rdb in rdb_files:
        analyze_rdb(str(rdb))
