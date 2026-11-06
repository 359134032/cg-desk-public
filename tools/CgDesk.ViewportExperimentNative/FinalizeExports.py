"""Append a read-only PE export section; never execute or modify the source DLL."""
import pathlib
import struct
import sys


def align(value, boundary):
    if boundary <= 0 or boundary & (boundary - 1):
        raise ValueError("Invalid PE alignment")
    return (value + boundary - 1) & ~(boundary - 1)


class Image:
    def __init__(self, data):
        self.data = data
        self.pe = self.u32(0x3C)
        if data[:2] != b"MZ" or data[self.pe:self.pe + 4] != b"PE\0\0" or self.u16(self.pe + 4) != 0x14C:
            raise ValueError("Expected x86 PE")
        self.optional = self.pe + 24
        if self.u16(self.optional) != 0x10B:
            raise ValueError("Expected PE32")
        self.count = self.u16(self.pe + 6)
        self.table = self.optional + self.u16(self.pe + 20)
        self.sections = []
        for i in range(self.count):
            at = self.table + i * 40
            virtual_size, rva, raw_size, raw = struct.unpack_from("<IIII", data, at + 8)
            self.sections.append((virtual_size, rva, raw_size, raw))

    def u16(self, at):
        return struct.unpack_from("<H", self.data, at)[0]

    def u32(self, at):
        return struct.unpack_from("<I", self.data, at)[0]

    def raw(self, rva):
        for size, address, raw_size, raw in self.sections:
            if address <= rva < address + max(size, raw_size):
                offset = raw + rva - address
                if offset >= len(self.data):
                    raise ValueError("RVA has no file bytes")
                return offset
        raise ValueError("Unmapped RVA")

    def string(self, at):
        return self.data[at:self.data.index(b"\0", at)].decode("ascii")

    def exports(self):
        directory = self.raw(self.u32(self.optional + 96))
        base, functions, names, eat, npt, ordinals = struct.unpack_from("<IIIIII", self.data, directory + 16)
        if functions > 4096 or names > 4096:
            raise ValueError("Excessive export table")
        eat, npt, ordinals = self.raw(eat), self.raw(npt), self.raw(ordinals)
        result = {}
        for i in range(names):
            name = self.string(self.raw(self.u32(npt + i * 4)))
            index = self.u16(ordinals + i * 2)
            result[name] = (base + index, self.u32(eat + index * 4))
        occupied = {base + i for i in range(functions) if self.u32(eat + i * 4)}
        if {ordinal for ordinal, _ in result.values()} != occupied:
            raise ValueError("Unnamed/aliased exports require explicit handling; refusing to guess")
        return result


def main(source_path, output_path, system_path):
    source, output, system = map(pathlib.Path, (source_path, output_path, system_path))
    if source.resolve() == output.resolve() or system.resolve() == output.resolve():
        raise ValueError("Output must be separate from both input DLLs")
    data = bytearray(source.read_bytes())
    image = Image(data)
    local = image.exports()
    original = Image(system.read_bytes()).exports()
    if "DirectDrawCreateEx" not in original or "DirectDrawCreateEx" not in local or "CgDeskViewportStatus" not in local:
        raise ValueError("Required real/local exports are missing")
    marker = next((value for name, value in local.items() if name.endswith("CgDeskViewportBuildMarker")), None)
    if not marker:
        raise ValueError("Build marker export is missing")
    exports = {}
    for name, (ordinal, _) in original.items():
        exports[name] = (ordinal, local[name][1] if name == "DirectDrawCreateEx" else f"cgdesk_viewport_sys.{name}")
    next_ordinal = max(item[0] for item in exports.values()) + 1
    exports["CgDeskViewportStatus"] = (next_ordinal, local["CgDeskViewportStatus"][1])
    exports["CgDeskViewportBuildMarker"] = (next_ordinal + 1, marker[1])
    file_alignment = image.u32(image.optional + 36)
    section_alignment = image.u32(image.optional + 32)
    first_raw = min(raw for _, _, size, raw in image.sections if size)
    section_header = image.table + image.count * 40
    if section_header + 40 > first_raw or section_header + 40 > image.u32(image.optional + 60):
        raise ValueError("No PE header room for a new export section")
    rva = align(max(address + max(size, raw_size) for size, address, raw_size, _ in image.sections), section_alignment)
    raw_offset = align(len(data), file_alignment)
    base = min(item[0] for item in exports.values())
    count = max(item[0] for item in exports.values()) - base + 1
    names = sorted(exports)
    payload = bytearray(40 + count * 4 + len(names) * 4 + len(names) * 2)
    eat = 40
    npt = eat + count * 4
    ordinals = npt + len(names) * 4

    def append_string(value):
        position = rva + len(payload)
        payload.extend(value.encode("ascii") + b"\0")
        return position

    dll_name = append_string("ddraw.dll")
    for index, name in enumerate(names):
        ordinal, target = exports[name]
        name_rva = append_string(name)
        target_rva = append_string(target) if isinstance(target, str) else target
        struct.pack_into("<I", payload, eat + (ordinal - base) * 4, target_rva)
        struct.pack_into("<I", payload, npt + index * 4, name_rva)
        struct.pack_into("<H", payload, ordinals + index * 2, ordinal - base)
    struct.pack_into("<IIHHIIIIIII", payload, 0, 0, 0, 0, 0, dll_name, base, count, len(names), rva + eat, rva + npt, rva + ordinals)
    virtual_size = len(payload)
    raw_size = align(virtual_size, file_alignment)
    struct.pack_into("<8sIIIIIIHHI", data, section_header, b".fwd\0\0\0\0", virtual_size, rva, raw_size, raw_offset, 0, 0, 0, 0, 0x40000040)
    struct.pack_into("<H", data, image.pe + 6, image.count + 1)
    struct.pack_into("<I", data, image.optional + 56, align(rva + virtual_size, section_alignment))
    struct.pack_into("<I", data, image.optional + 8, image.u32(image.optional + 8) + raw_size)
    struct.pack_into("<II", data, image.optional + 96, rva, virtual_size)
    struct.pack_into("<I", data, image.optional + 64, 0)  # Unsigned new experimental DLL, no inherited checksum.
    data.extend(b"\0" * (raw_offset - len(data)))
    data.extend(payload)
    data.extend(b"\0" * (raw_size - virtual_size))
    rebuilt = Image(data).exports()
    if set(rebuilt) != set(exports) or any(rebuilt[name][0] != exports[name][0] for name in exports):
        raise ValueError("Final export table validation failed")
    output.write_bytes(data)
    print(f"Finalized {len(original) - 1} true system forwarders and 3 local exports in read-only .fwd section.")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("Usage: FinalizeExports.py <local-only-dll> <separate-output-dll> <local-system-ddraw>")
    main(*sys.argv[1:])
