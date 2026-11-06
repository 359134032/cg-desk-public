"""Execution-free audit of the hash-bound runtime .text capture.

This reads bytes and a dumpbin listing. It never loads or launches game code,
attaches to a process, or changes a game file. The generated plan is research
evidence, not a deployable patch profile.
"""
from __future__ import annotations

import hashlib
import bisect
import json
import re
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RESEARCH = ROOT / "artifacts" / "viewport-research"
CAPTURE = RESEARCH / "runtime-72316-20261106-042053-1f8731d2"
BASE = 0x400000
TEXT = 0x401000
CACHE_SIZE = 0x1452
NEW_STRIDE = 64
LAYERS = {
    0xA547F8: [0x49BE81, 0x49BED1, 0x49BFEE, 0x49C648, 0x49C698,
               0x49C78B, 0x49CB28, 0x49CB74, 0x49CC25],
    0xA55C50: [0x49BE7C, 0x49BECC, 0x49C0E6, 0x49C643, 0x49C693,
               0x49C834, 0x49CA21, 0x49CB23, 0x49CB6F],
    0xA570A8: [0x499FE9, 0x49A019, 0x49BE77, 0x49BEC7, 0x49C63E,
               0x49C68E, 0x49CB1E, 0x49CB6A, 0x4A09F2],
    0xA58500: [0x49AA65, 0x49AABE, 0x49B093, 0x49BEC2, 0x49C689,
               0x49CB65, 0x49CF43, 0x49D037, 0x49D08D, 0x49D374,
               0x49D475],
}
INTERIORS = {0x49AC91: 0xA570CE, 0x49AD0C: 0xA570CE,
             0x49AE7A: 0xA570FA, 0x49AEFA: 0xA570CE}
CLEAR_CALLS = [0x49FA67, 0x49FA79, 0x49FA8B, 0x49F513, 0x49F90F]
BOUNDS = {0x4A0548: (0xE2, 0xDC), 0x4A0554: (0xEC, 0xE4),
          0x4A0566: (0x15, 0x1C), 0x4A0578: (0x1F, 0x24)}
BOUNDS_CALLS = [0x49A191, 0x49A241, 0x49A341, 0x49D8A2, 0x49F0A4,
                0x49F4D2, 0x49FE7E, 0x5471DC, 0x54C5C3, 0x557556]
SCAN_CALLS = [0x49BF07, 0x49C6CE, 0x49EAD2]


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def has_literal(text: str, value: int) -> bool:
    return value in [int(s, 16) for s in re.findall(r"\b([0-9A-F]+)h\b", text)]


def instructions(path: Path) -> list[dict]:
    found = []
    pattern = re.compile(r"^\s+([0-9A-F]{8}): ((?:[0-9A-F]{2}(?: |$))+)(.*)$")
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        m = pattern.match(line)
        if not m:
            continue
        address = int(m[1], 16)
        raw = bytes.fromhex(m[2])
        text = m[3].strip()
        if not text and found and found[-1]["address"] + len(found[-1]["raw"]) == address:
            found[-1]["raw"] += raw
        else:
            found.append({"address": address, "raw": raw, "text": text})
    return found


def cache_rect(px: int, py: int, map_w: int, map_h: int, expanded: bool) -> tuple[int, int, int, int]:
    left, right, top, bottom = (36, 28, 28, 36) if expanded else (30, 21, 20, 31)
    return max(0, px - left), max(0, py - top), min(map_w, px + right), min(map_h, py + bottom)


def main() -> None:
    image = (CAPTURE / "partial-image.exe").read_bytes()
    code = (CAPTURE / "code-section-00-text.bin").read_bytes()
    check(digest(image) == "b95fdfaa61520908ed1a7b04fe75c74decb53a96860d17f25b8aef8a6a2b2636", "image identity")
    check(digest(code) == "1e7f13e748c792586c7db2eb21d26d19f710bafecea02df6c2d6e44ad136d427", "text identity")
    listing = instructions(RESEARCH / "map-audit-disassembly.txt")
    owner = {}
    for ins in listing:
        if TEXT <= ins["address"] < TEXT + len(code):
            for off in range(len(ins["raw"])):
                owner[ins["address"] + off] = ins
    expected = {operand: base for base, refs in LAYERS.items() for operand in refs}
    expected.update(INTERIORS)
    check(len(expected) == 42, "42 known pointer operands")
    refs = []
    coincidences = []
    unknown = []
    for off in range(len(code) - 3):
        value = struct.unpack_from("<I", code, off)[0]
        if not any(base <= value < base + CACHE_SIZE for base in LAYERS):
            continue
        address = TEXT + off
        ins = owner.get(address)
        record = {"operand": hex(address), "value": hex(value),
                  "instruction": hex(ins["address"]) if ins else None,
                  "disassembly": ins["text"] if ins else None}
        if address in expected:
            check(value == expected[address], f"expected pointer at {address:#x}")
            check(ins is not None and has_literal(ins["text"], value), f"decoded pointer at {address:#x}")
            record["instruction_bytes"] = ins["raw"].hex()
            record["class"] = "interior-translation-unresolved" if address in INTERIORS else "direct-base"
            refs.append(record)
        elif ins is not None and not has_literal(ins["text"], value):
            record["class"] = "unaligned-instruction-byte-coincidence"
            coincidences.append(record)
        else:
            record["class"] = "unresolved"
            unknown.append(record)
    check({int(x["operand"], 16) for x in refs} == set(expected), "all known refs found")
    check(not unknown, "unclassified .text cache reference")
    pe = struct.unpack_from("<I", image, 0x3C)[0]
    count = struct.unpack_from("<H", image, pe + 6)[0]
    optional_size = struct.unpack_from("<H", image, pe + 20)[0]
    section_table = pe + 24 + optional_size
    uncaptured_matches = []
    address_taken_scan = []
    instruction_starts = [item["address"] for item in listing]
    sections = []
    for n in range(count):
        item = section_table + 40 * n
        name = image[item:item + 8].rstrip(b"\0").decode("ascii")
        virtual_size, rva, raw_size, raw_offset = struct.unpack_from("<IIII", image, item + 8)
        sections.append({"name": name, "virtual_size": virtual_size, "raw_size": raw_size,
                         "captured_runtime_bytes": name == ".text"})
        for off in range(raw_size - 3):
            value = struct.unpack_from("<I", image, raw_offset + off)[0]
            if value in (0x4A0540, 0x49AB80):
                address_taken_scan.append({"section": name, "address": hex(BASE + rva + off), "value": hex(value)})
            if name != ".text" and any(start <= value < start + CACHE_SIZE for start in LAYERS):
                address = BASE + rva + off
                index = bisect.bisect_right(instruction_starts, address) - 1
                ins = listing[index] if index >= 0 else None
                decoded = ins is not None and ins["address"] <= address and address + 4 <= ins["address"] + len(ins["raw"])
                uncaptured_matches.append({"section": name, "address": hex(address), "value": hex(value),
                                           "class": "instruction-byte-coincidence" if decoded and not has_literal(ins["text"], value) else "opaque-uncaptured-data",
                                           "disassembly": ins["text"] if decoded else None})
    check(not address_taken_scan, "no literal address-taken facade target in available raw sections")
    # The profile does NOT use the tempting fixed interior translation above.
    # Interior operands point at a dedicated legacy51 scratch image; guarded
    # call-site facades restore the original bounds only during that scan.
    profile = ["// Generated from the hash-bound runtime capture by AuditMapCache.py.",
               "#pragma once", "struct MapCachePatchSpec { uintptr_t operand; uintptr_t guard; unsigned kind; unsigned layer; unsigned offset; unsigned char bytes[16]; };",
               "static const MapCachePatchSpec MapCachePatchSpecs[] = {"]
    for layer, (base, operands) in enumerate(LAYERS.items()):
        for operand in operands:
            guard = owner[operand]["address"]
            raw = code[guard - TEXT:guard - TEXT + 16]
            profile.append("    {0x%x,0x%x,0,%d,0,{%s}}," % (operand, guard, layer, ",".join("0x%02x" % b for b in raw)))
    for operand, value in INTERIORS.items():
        guard = owner[operand]["address"]
        raw = code[guard - TEXT:guard - TEXT + 16]
        profile.append("    {0x%x,0x%x,1,0,0x%x,{%s}}," % (operand, guard, value - 0xA570A8, ",".join("0x%02x" % b for b in raw)))
    for kind, calls, target in [(2, CLEAR_CALLS, 0x57E7C0), (3, BOUNDS_CALLS, 0x4A0540), (4, SCAN_CALLS, 0x49AB80)]:
        for call in calls:
            raw = code[call - TEXT:call - TEXT + 16]
            check(raw[0] == 0xE8 and call + 5 + struct.unpack_from("<i", raw, 1)[0] == target, "facade original direct target")
            profile.append("    {0x%x,0x%x,%d,0,0,{%s}}," % (call + 1, call, kind, ",".join("0x%02x" % b for b in raw)))
    profile.append("};")
    # Complete original routines whose behavior the facades depend on.
    for name, start, end in [("LegacyScan", 0x49AB80, 0x49B048), ("LegacyBounds", 0x4A0540, 0x4A0619),
                             ("GameClear", 0x57E7C0, 0x57E91A)]:
        raw = code[start - TEXT:end - TEXT]
        profile.append("static const uintptr_t MapCache%sAddress = 0x%x;" % (name, start))
        profile.append("static const unsigned char MapCache%sBytes[] = {%s};" % (name, ",".join("0x%02x" % b for b in raw)))
    dependencies = [("CoordDifference", 0x42CB40, 0x42CB86), ("CoordMinus", 0x42CC10, 0x42CC51),
                    ("CoordPlus", 0x42CD70, 0x42CDB1), ("CoordCompare", 0x42D050, 0x42D099),
                    ("ClampRequest", 0x49AB30, 0x49AB77), ("CoordRand", 0x584D3A, 0x584D5B)]
    for name, start, end in dependencies:
        raw = code[start - TEXT:end - TEXT]
        profile.append("static const unsigned char MapCache%sBytes[] = {%s};" % (name, ",".join("0x%02x" % b for b in raw)))
    profile.append("struct MapCacheDependencySpec { uintptr_t address; const unsigned char* bytes; size_t count; };")
    profile.append("static const MapCacheDependencySpec MapCacheDependencies[] = {")
    for name, start, _ in dependencies:
        profile.append("    {0x%x, MapCache%sBytes, sizeof(MapCache%sBytes)}," % (start, name, name))
    profile.append("};")
    (Path(__file__).parent / "MapCacheProfile.h").write_text("\n".join(profile) + "\n", encoding="utf-8")
    clear_guards = []
    for call in CLEAR_CALLS:
        raw = code[call - TEXT:call - TEXT + 5]
        check(raw[0] == 0xE8, "memset direct E8 call")
        target = call + 5 + struct.unpack_from("<i", raw, 1)[0]
        check(target == 0x57E7C0, "original memset target")
        clear_guards.append({"call": hex(call), "bytes": raw.hex(), "target": hex(target)})
    for address, (old, _) in BOUNDS.items():
        check(code[address - TEXT] == old, f"bounds operand {address:#x}")
        check(code[address - TEXT - 1] == 0x6A, "bounds PUSH imm8")
    geometry = []
    for width, height in [(1024, 768), (1152, 864), (1280, 960), (1400, 1050),
                          (1440, 1080), (1600, 1200), (1920, 1080)]:
        extent = width / 128 + height / 96
        geometry.append({"width": width, "height": height,
                         "maximum_tile_axis_offset": extent,
                         "old_short_axis_margin": 20 - extent,
                         "proposed_short_axis_margin": 28 - extent,
                         "status": "geometry-only-not-runtime-validation"})
    # Prove the tempting fixed +0x40c translation works only at full 64 stride.
    good = 0
    for row in range(9, 32):
        for col in range(19, 42):
            old_index = row * NEW_STRIDE + col
            translated = old_index + 6 + 8 * NEW_STRIDE
            check(translated == (row + 8) * NEW_STRIDE + col + 6, "full stride translation")
            check(0 <= translated < NEW_STRIDE * NEW_STRIDE, "translated access bounded")
            good += 1
    # At x=30 on a 200x200 map, the original full51 guard permits entry;
    # the expanded cache is clipped to58 columns and offsetX is0, not6.
    px, py = 30, 80
    old = cache_rect(px, py, 200, 200, False)
    new = cache_rect(px, py, 200, 200, True)
    stride = new[2] - new[0]
    row, col = 9, 19
    fixed_index = row * stride + col + 6 + 8 * NEW_STRIDE
    correct_index = (row + old[1] - new[1]) * stride + col + old[0] - new[0]
    check(old[2] - old[0] == 51 and old[3] - old[1] == 51, "old51 guard permits")
    check(stride == 58 and fixed_index != correct_index, "counterexample must reject fixed offsets")
    report = {
        "purpose": "offline-audit-only; no game code executed and no runtime patch implemented",
        "image_sha256": digest(image), "text_sha256": digest(code),
        "captured_scope": "runtime .text only; original packed sections and uncaptured virtual .data do not prove live pointer-table completeness",
        "direct_base_reference_count": sum(map(len, LAYERS.values())),
        "interior_reference_count": len(INTERIORS), "pointer_operands": refs,
        "raw_byte_coincidences": coincidences, "unclassified_text_references": unknown,
        "sections": sections, "uncaptured_section_raw_matches": uncaptured_matches,
        "literal_facade_target_address_taken_matches": address_taken_scan,
        "implemented_facade_patch_count": 60,
        "clear_call_guards": clear_guards,
        "bounds_candidates": [{"operand": hex(a), "old": hex(v[0]), "candidate": hex(v[1])} for a, v in BOUNDS.items()],
        "geometry": geometry,
        "full64_translation_checks": good,
        "clipped_cache_counterexample": {"player": [px, py], "map": [200, 200],
                                         "old_rect": old, "new_rect": new, "new_stride": stride,
                                         "old_row_col": [row, col], "tempting_fixed_translation_index": fixed_index,
                                         "correct_index": correct_index},
        "deployment_ready": False,
        "blocking_questions": [
            "Preserve 0x49ab80 old51 guard and the world cells used for original request generation under all map-edge clamping cases.",
            "Resolve any live pointer tables or computed base derivations outside the captured .text.",
            "Prove initialization-only transaction does not race an existing map consumer or memset wrapper call.",
        ],
    }
    output = RESEARCH / "map-cache-audit-plan.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "pointer_operands": len(refs),
                      "coincidences": len(coincidences), "geometry_cases": len(geometry),
                      "uncaptured_raw_matches": len(uncaptured_matches),
                      "full64_translation_checks": good, "clipped_counterexample_confirmed": True,
                      "deployment_ready": False}, ensure_ascii=False))


if __name__ == "__main__":
    main()
