"""Offline model of the captured strip-transition initializers; executes no game code."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CAPTURE = ROOT / 'artifacts/viewport-research/runtime-72316-20261106-042053-1f8731d2'
EXPECTED = '1e7f13e748c792586c7db2eb21d26d19f710bafecea02df6c2d6e44ad136d427'


def initialize(height, isolated):
    """Match MOVUPS group ordering and REP STOS remainder, 558CC0/559A10/559D10."""
    sentinel = -987654.0
    cells = [sentinel] * (3 * 2048 + height + 16)
    position = 2048 if isolated else 1024
    speed = 4096 if isolated else 2048
    following = 3072
    top = height - height % 16
    for block in range(0, top, 16):
        for lane in (0, 4, 8, 12):
            i = block + lane
            cells[speed+i:speed+i+4] = [0.0] * 4
            cells[position+i:position+i+4] = [float(n) for n in range(i, i+4)]
    cells[speed+top:speed+height] = [0.0] * (height-top)
    for i in range(top, height):
        cells[position+i] = float(i)
    return {
        'height': height,
        'layout': 'independent_2048_float_regions' if isolated else 'legacy_1024_float_spacing',
        'incorrect_position_rows': [i for i in range(height) if cells[position+i] != float(i)],
        'incorrect_speed_rows': [i for i in range(height) if cells[speed+i] != 0.0],
        'speed_first_8': cells[speed:speed+8],
        'following_effect_scalar_overwritten': not isolated and cells[following] != sentinel,
        'following_effect_init_flag_overwritten': not isolated and cells[following+1] != sentinel,
        # Mirror updates explicitly index height, despite initializing only [0,height).
        'highest_mirror_index': height,
        'mirror_index_inside_original_capacity': height < 1024,
        'mirror_index_inside_relocated_capacity': height < 2044,
    }


def main():
    actual = hashlib.sha256((CAPTURE / 'code-section-00-text.bin').read_bytes()).hexdigest()
    assert actual == EXPECTED
    results = [initialize(h, isolated) for h in (600, 960, 1023, 1024, 1050, 1080, 1200)
               for isolated in (False, True)]
    for r in results:
        if r['layout'].startswith('independent'):
            assert not r['incorrect_position_rows'] and not r['incorrect_speed_rows']
        elif r['height'] > 1024:
            assert r['incorrect_speed_rows'] == list(range(r['height']-1024))
            assert r['following_effect_scalar_overwritten']
    proof = {
        'source_text_sha256': actual,
        'evidence_kind': 'static_instruction_audit_plus_synthetic_memory_model',
        'instruction_ranges': ['558CC0..558D83', '559A10..559AD3', '559D10..559DD3'],
        'mirror_access_ranges': ['558E04..558E20', '5598C5..559901', '559C38..559C58'],
        'old_bases': ['010C9868', '010CA868', '010CB868'],
        'following_effect_state': {'scalar': '010CC868', 'initialization_flag': '010CC86C',
                                   'control_table': '010CC870'},
        'limitations': ['Does not execute the captured client or prove any transition completes.',
                        'Does not model arbitrary corruption predating the initializer.',
                        'Profile coverage independently scanned from dumpbin disassembly; runtime validation still required.'],
        'results': results,
    }
    output = ROOT / 'artifacts/viewport-research/transition-aliasing-independent-audit.json'
    output.write_text(json.dumps(proof, indent=2), encoding='utf-8')
    print('14 initialization models passed: heights 600, 960, 1023, 1024, 1050, 1080, 1200; no target execution.')
    print(output)


if __name__ == '__main__':
    main()
