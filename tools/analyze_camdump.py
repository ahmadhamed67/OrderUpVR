"""Offline search of orderup_camdump.bin.z (written by the mod's camera research).

The file holds two copies of every game-memory page that changed while the camera moved (plus pages
holding field-of-view values), with the camera before (A) and after (B). A real camera value must match
camera A in copy A *and* camera B in copy B, so chance matches are ruled out.

Usage: python analyze_camdump.py orderup_camdump.bin.z
"""
import math
import struct
import sys
import zlib


def load(path):
    raw = zlib.decompress(open(path, 'rb').read())
    assert raw[:8] == b'OUVRCAM1', 'not a camera capture'
    count, = struct.unpack_from('<I', raw, 8)
    cam_a = struct.unpack_from('<27f', raw, 12)
    cam_b = struct.unpack_from('<27f', raw, 12 + 108)
    pages, off = [], 12 + 216
    for _ in range(count):
        addr, flags = struct.unpack_from('<II', raw, off)
        a = struct.unpack_from('>1024f', raw, off + 8)
        b = struct.unpack_from('>1024f', raw, off + 8 + 4096)
        pages.append((addr, flags, a, b))
        off += 8 + 8192
    return cam_a, cam_b, pages


def forms(cam):
    """Every form of the camera the mod also searches live, plus a few more: name -> list of floats."""
    R = [cam[0:3], cam[3:6], cam[6:9]]
    sx, sy = cam[9], cam[10]
    out = {}
    rows = [v for r in R for v in r]
    cols = [R[r][c] for c in range(3) for r in range(3)]
    out['view rows'] = rows
    out['view transposed'] = cols
    out['view rows (-z)'] = rows[:6] + [-v for v in rows[6:]]
    out['view transposed (-z)'] = [(-v if i % 3 == 2 else v) for i, v in enumerate(cols)]
    fwd = [-v for v in R[2]]
    out['forward vector'] = fwd
    out['backward vector'] = list(R[2])
    out['right vector'] = list(R[0])
    out['up vector'] = list(R[1])
    (a00, a01, a02), (a10, a11, a12), (a20, a21, a22) = R
    tr = a00 + a11 + a22
    if tr > 0:
        s = math.sqrt(tr + 1) * 2; q = ((a21 - a12) / s, (a02 - a20) / s, (a10 - a01) / s, 0.25 * s)
    elif a00 > a11 and a00 > a22:
        s = math.sqrt(1 + a00 - a11 - a22) * 2; q = (0.25 * s, (a01 + a10) / s, (a02 + a20) / s, (a21 - a12) / s)
    elif a11 > a22:
        s = math.sqrt(1 + a11 - a00 - a22) * 2; q = ((a01 + a10) / s, 0.25 * s, (a12 + a21) / s, (a02 - a20) / s)
    else:
        s = math.sqrt(1 + a22 - a00 - a11) * 2; q = ((a02 + a20) / s, (a12 + a21) / s, 0.25 * s, (a10 - a01) / s)
    x, y, z, w = q
    out['quaternion xyzw'] = [x, y, z, w]
    out['quaternion xyzw (inverse)'] = [-x, -y, -z, w]
    out['quaternion wxyz'] = [w, x, y, z]
    out['quaternion wxyz (inverse)'] = [w, -x, -y, -z]
    yaw = math.atan2(fwd[0], fwd[2]); yaw2 = math.atan2(-fwd[0], -fwd[2]); yaw3 = math.atan2(fwd[2], fwd[0])
    pitch = math.asin(max(-1, min(1, fwd[1])))
    for name, v in (('yaw', yaw), ('yaw (+180)', yaw2), ('yaw (other axis)', yaw3), ('pitch', pitch)):
        out[name + ' rad'] = [v]; out[name + ' rad (negated)'] = [-v]
        out[name + ' deg'] = [math.degrees(v)]; out[name + ' deg (negated)'] = [-math.degrees(v)]
    fy, fx = 2 * math.atan(1 / sy), 2 * math.atan(1 / sx)
    for name, v in (('projection sy', sy), ('projection sx', sx), ('tan half fov y', 1 / sy), ('tan half fov x', 1 / sx),
                    ('fov y rad', fy), ('fov y deg', math.degrees(fy)), ('half fov y rad', fy / 2), ('half fov y deg', math.degrees(fy / 2)),
                    ('fov x rad', fx), ('fov x deg', math.degrees(fx))):
        out[name] = [v]
    return out


def tol(name, v):
    if name.startswith(('projection', 'tan', 'fov', 'half fov')):
        return max(2e-5, abs(v) * 4e-5)
    if ' deg' in name:
        return 0.03
    if len(name) and (' rad' in name):
        return 5e-4
    return 4e-3


def matches(words, i, want, name, strides=(1,)):
    n = len(want)
    for stride in strides:
        idx = [i + (k // 3) * stride + k % 3 for k in range(n)] if stride != 1 else list(range(i, i + n))
        if idx[-1] >= len(words):
            continue
        ok = True
        for j, w in zip(idx, want):
            v = words[j]
            if not (v == v) or abs(v - w) > tol(name, w):
                ok = False
                break
        if ok:
            return stride
    return None


def main():
    cam_a, cam_b, pages = load(sys.argv[1])
    fa, fb = forms(cam_a), forms(cam_b)
    print('pages: %d (%d changed with the camera)' % (len(pages), sum(1 for p in pages if p[1] & 1)))
    print('camera A forward %s fov-y %.3f deg' % (['%.4f' % v for v in fa['forward vector']], fa['fov y deg'][0]))
    print('camera B forward %s fov-y %.3f deg' % (['%.4f' % v for v in fb['forward vector']], fb['fov y deg'][0]))
    found = []
    for addr, flags, a, b in pages:
        for i in range(1024):
            for name in fa:
                wa, wb = fa[name], fb[name]
                if len(wa) == 1 and abs(wa[0]) < 1e-3:
                    continue
                strides = (3, 4) if len(wa) == 9 else (1,)
                if len(wa) == 9:
                    s = matches(a, i, wa, name, strides)
                    if s and matches(b, i, wb, name, (s,)):
                        found.append((addr + i * 4, name + ' stride %d' % s))
                    continue
                for sign in ((1, -1) if name.startswith('quaternion') else (1,)):
                    if matches(a, i, [sign * v for v in wa], name) and any(matches(b, i, [sg * v for v in wb], name) for sg in (1, -1)):
                        found.append((addr + i * 4, name))
                        break
    for addr, name in found:
        print('0x%08x  %s' % (addr, name))
    if not found:
        print('no camera form matched in both copies')

    # Fallback for a form nobody predicted: unit-length directions that turned by the same angle as the camera
    fwa, fwb = fa['forward vector'], fb['forward vector']
    turn = math.degrees(math.acos(max(-1, min(1, sum(x * y for x, y in zip(fwa, fwb))))))
    print('\ncamera turned %.2f degrees; directions in memory that turned the same:' % turn)
    shown = 0
    for addr, flags, a, b in pages:
        if not flags & 1:
            continue
        for i in range(1022):
            va, vb = a[i:i + 3], b[i:i + 3]
            if any(v != v for v in va + vb):
                continue
            la, lb = math.sqrt(sum(v * v for v in va)), math.sqrt(sum(v * v for v in vb))
            if abs(la - 1) > 0.01 or abs(lb - 1) > 0.01:
                continue
            ang = math.degrees(math.acos(max(-1, min(1, sum(x * y for x, y in zip(va, vb)) / (la * lb)))))
            if abs(ang - turn) < 0.3 and ang > 0.5:
                print('0x%08x  (%.3f %.3f %.3f) -> (%.3f %.3f %.3f)  turned %.2f' % ((addr + i * 4,) + tuple(va) + tuple(vb) + (ang,)))
                shown += 1
                if shown >= 60:
                    return


if __name__ == '__main__':
    main()
