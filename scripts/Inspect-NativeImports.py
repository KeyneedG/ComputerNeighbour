"""Read PE import and delay-import directories without third-party packages."""
import json
import struct
import sys
from pathlib import Path


def imports(path):
    data = path.read_bytes()
    if data[:2] != b"MZ":
        return []
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return []
    sections_count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_length = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directories = optional + (112 if magic == 0x20B else 96)
    image_base = struct.unpack_from("<Q" if magic == 0x20B else "<I", data, optional + (24 if magic == 0x20B else 28))[0]
    sections = []
    for index in range(sections_count):
        offset = optional + optional_length + index * 40
        virtual_size, rva, size, raw = struct.unpack_from("<IIII", data, offset + 8)
        sections.append((rva, max(virtual_size, size), raw))

    def at(rva):
        for start, size, raw in sections:
            if start <= rva < start + size:
                return raw + rva - start
        if 0 <= rva < len(data):
            return rva
        raise ValueError(f"Invalid PE RVA {rva:x}")

    def name(rva):
        offset = at(rva)
        end = data.index(b"\0", offset)
        return data[offset:end].decode("ascii").lower()

    results = set()
    imports_rva, imports_size = struct.unpack_from("<II", data, directories + 8)
    if imports_rva:
        offset = at(imports_rva)
        for index in range(max(imports_size // 20, 1) + 1):
            fields = struct.unpack_from("<IIIII", data, offset + index * 20)
            if not any(fields):
                break
            results.add(name(fields[3]))
    delay_rva, delay_size = struct.unpack_from("<II", data, directories + 13 * 8)
    if delay_rva:
        offset = at(delay_rva)
        for index in range(max(delay_size // 32, 1) + 1):
            fields = struct.unpack_from("<IIIIIIII", data, offset + index * 32)
            if not any(fields):
                break
            results.add(name(fields[1] if fields[0] & 1 else fields[1] - image_base))
    return sorted(results)


root = Path(__file__).resolve().parents[1]
folders = sys.argv[1:] or ["runtimes/llama", "runtimes/whisper", "runtimes/piper", "app"]
result = {}
for folder in folders:
    for path in sorted((root / folder).rglob("*")):
        if path.suffix.lower() in {".exe", ".dll"}:
            result[str(path.relative_to(root))] = imports(path)
print(json.dumps(result, indent=2))
