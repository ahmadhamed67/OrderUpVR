"""Order Up!! WAD ('0DAW' v4) reader. Names are CRC32 (no final xor) of the path."""
import struct, zlib, sys

CHEF = r'C:/Emulator/Games/Order Up!! (Europe) (En,Fr,De,Es,It)/PS3_GAME/USRDIR/Chef.wad'


def nhash(name):
    return zlib.crc32(name.encode() if isinstance(name, str) else name) ^ 0xffffffff


def parse(buf):
    """Return (header, {hash: (offset, size)}, data_base) for a WAD held in buf."""
    magic, ver, n, tsize, a, comp, usize, csize = struct.unpack_from('<4s7I', buf, 0)
    assert magic == b'0DAW', magic
    hashes = [struct.unpack_from('<II', buf, 0x30 + i * 8) for i in range(n)]
    t2 = 0x30 + n * 8
    locs = [struct.unpack_from('<II', buf, t2 + i * 8) for i in range(n)]
    return (n, tsize, a, comp, usize, csize), {h: locs[i] for h, i in hashes}, locs


def open_chef():
    data = open(CHEF, 'rb').read()
    return data, parse(data)


def inner(data, off, size):
    """A nested WAD: header + table, then (if compressed) one zlib stream holding the files."""
    b = data[off:off + size]
    hdr, table, locs = parse(b)
    n, tsize, a, comp, usize, csize = hdr
    body = zlib.decompress(b[tsize:]) if comp else b[tsize:]
    return hdr, table, body


if __name__ == '__main__':
    data, (hdr, table, locs) = open_chef()
    print('outer entries', len(table))
    for nm in sys.argv[1:]:
        h = nhash(nm)
        if h not in table:
            print(nm, 'not found'); continue
        off, size = table[h]
        print(nm, hex(off), size)
