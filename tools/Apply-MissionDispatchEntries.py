"""Restore confirmed mission state-table entries in the local AOT generation."""

import argparse
import hashlib
import re
import struct
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "iso/local-analysis"
GEN = LOCAL / "xboxrecomp-generated-xpp-untraced-current/gen"
sys.path[:0] = [str(ROOT / "local-tools" / name)
                for name in ("python-packages", "xboxrecomp")]

from capstone import CS_ARCH_X86, CS_GRP_CALL, CS_GRP_JUMP, CS_MODE_32, Cs
from capstone.x86 import X86_OP_IMM
from tools.recomp.config import configure_from_xbe
from tools.recomp.translator import FunctionTranslator


XBE_SHA256 = "1d4e006a2eab0694b32562c453fdeb4c3b077eec8c6ed6b75bd6fe4059d8200a"
REGIONS = (
    (0x0009D3B0, 0x0009D580, 0x002D4124, 0x0009DA43),
    (0x0009DAA0, 0x0009DD80, 0x002D4120, 0x0009DA43),
    (0x000A11A0, 0x000A14B7, 0x002D4C80, 0x000A10F7),
)
TABLE = "static const recomp_entry_t g_recomp_table[] = {\n"
ENTRY = re.compile(r"^    \{ 0x([0-9A-F]+)u,.*\n", re.M)
PLACEHOLDER = "void sub_0009D3B0(void) { g_esp += 4; /* 0x0009D3B0: not detected */ }\n"


def name(address):
    return f"sub_{address:08X}"


def translate_entries(xbe_path, dispatch):
    xbe = xbe_path.read_bytes()
    if hashlib.sha256(xbe).hexdigest() != XBE_SHA256:
        raise ValueError("The XBE differs from the validated edition")
    base, = struct.unpack_from("<I", xbe, 0x104)
    count, table = struct.unpack_from("<II", xbe, 0x11C)
    sections = [struct.unpack_from("<5I", xbe, table - base + 56 * i)
                for i in range(count)]

    def read(address, length, executable=False):
        for flags, start, virtual_size, raw, raw_size in sections:
            offset = address - start
            if (0 <= offset and offset + length <= min(virtual_size, raw_size)
                    and (not executable or flags & 4)):
                return xbe[raw + offset:raw + offset + length]
        raise ValueError(f"Invalid XBE region {address:08X}+{length:X}")

    decoder = Cs(CS_ARCH_X86, CS_MODE_32)
    decoder.detail = True
    known = {int(row, 16) for row in ENTRY.findall(dispatch)}
    starts = {start for start, _, _, _ in REGIONS}
    functions = {target: {"start": target, "_addr": target, "end": target,
                          "name": name(target)} for target in known}
    originals = {}
    for start, end, slot, caller in REGIONS:
        if struct.unpack("<I", read(slot, 4))[0] != start:
            raise ValueError(f"State-table slot {slot:08X} changed")
        caller_insn, = decoder.disasm(read(caller, 7, True), caller)
        if (caller_insn.mnemonic not in {"call", "jmp"} or
                caller_insn.operands[0].mem.disp != slot - (4 if start == 0x9D3B0 else 0)):
            raise ValueError(f"State-table dispatch {caller:08X} changed")
        raw = read(start, end - start, True)
        instructions = list(decoder.disasm(raw, start))
        addresses = {i.address for i in instructions}
        if instructions[-1].address + instructions[-1].size != end:
            raise ValueError(f"Incomplete instruction at {end:08X}")
        for i in instructions:
            if not (i.group(CS_GRP_CALL) or i.group(CS_GRP_JUMP)):
                continue
            operand = i.operands[0]
            if operand.type != X86_OP_IMM:
                raise ValueError(f"Unverified dynamic edge at {i.address:08X}")
            if operand.imm not in addresses | known | starts:
                raise ValueError(f"Unresolved edge at {i.address:08X} to {operand.imm:08X}")
        originals[start] = raw
        functions[start] = {"start": start, "_addr": start, "end": end,
                            "name": name(start), "external_entry": True}

    configure_from_xbe(str(xbe_path))
    translator = FunctionTranslator(xbe, functions)
    bodies = {}
    for start in sorted(starts):
        body = translator.translate_function(start, functions[start])
        if not body or "RECOMP_UNIMPL" in body:
            raise ValueError(f"Incomplete translation at {start:08X}")
        bodies[start] = body.rstrip()
    return bodies, originals


def update_sources(code, dispatch, stubs, bodies):
    if dispatch.count(TABLE) != 1:
        raise ValueError("Generated dispatch table boundary changed")
    # The detector merged these entries behind unconditional tail jumps.
    # Keep existing entries, including the separately referenced 0x9DD19.
    for address, body in bodies.items():
        signature = f"void {name(address)}(void)"
        if signature + "\n{" in code:
            if body not in code:
                raise ValueError(f"The installed translation of {name(address)} differs")
        else:
            code += "\n" + body + "\n"
        entry = f"    {{ 0x{address:08X}u, (recomp_func_t){name(address)} }},\n"
        if entry not in dispatch:
            dispatch = dispatch.replace(TABLE, f"extern {signature};\n" + TABLE, 1)
            following = next(row for row in ENTRY.finditer(dispatch, dispatch.index(TABLE))
                             if int(row[1], 16) > address)
            dispatch = dispatch[:following.start()] + entry + dispatch[following.start():]
    if PLACEHOLDER in stubs:
        stubs = stubs.replace(PLACEHOLDER, "", 1)
    elif "void sub_0009D3B0(" in stubs:
        raise ValueError("An unexpected implementation occupies the unresolved stub")
    return code, dispatch, stubs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    paths = [GEN / file for file in
             ("recomp_0003.c", "recomp_dispatch.c", "recomp_stubs_unresolved.c")]
    before = tuple(path.read_text(encoding="utf-8") for path in paths)
    bodies, _ = translate_entries(LOCAL / "game_files/default.xbe", before[1])
    after = update_sources(*before, bodies)
    for path, old, new in zip(paths, before, after):
        if args.apply and old != new:
            pending = path.with_suffix(".c.pending")
            try:
                pending.write_text(new, encoding="utf-8", newline="\n")
                pending.replace(path)
            finally:
                pending.unlink(missing_ok=True)
        print(f"{path.name}: " + ("present" if old == new else
                                 "applied" if args.apply else "missing"))
    return int(before != after and not args.apply)


if __name__ == "__main__":
    raise SystemExit(main())
