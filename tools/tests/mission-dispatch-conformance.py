"""Compare the installed mission case handlers with the original x86."""

import os
import re
import subprocess
import tempfile
from pathlib import Path
from runpy import run_path


seed = run_path(str(Path(__file__).resolve().parents[1] / "Apply-MissionDispatchEntries.py"))
from capstone.x86 import X86_OP_IMM, X86_OP_MEM

generation = seed["GEN"]
code, dispatch, stubs = (path.read_text(encoding="utf-8") for path in
                         (generation / filename for filename in
                          ("recomp_0003.c", "recomp_dispatch.c", "recomp_stubs_unresolved.c")))
bodies, originals = seed["translate_entries"](seed["LOCAL"] / "game_files/default.xbe", dispatch)
if seed["update_sources"](code, dispatch, stubs, bodies) != (code, dispatch, stubs):
    raise SystemExit("The original mission callbacks are missing from the active dispatch")

HARNESS = r"""
#include <windows.h>
#include <stdio.h>
#define RECOMP_GENERATED_CODE
#include "recomp_types.h"

ptrdiff_t g_xbox_mem_offset;
RECOMP_TLS uint32_t g_eax, g_ebx, g_ecx, g_edx, g_esi, g_edi, g_esp, g_ebp, g_seh_ebp;

DEPENDENCIES
LIFTED

#undef esp
#undef eax
#undef ebx
#undef ecx
#undef edx
#undef esi
#undef edi

ORIGINALS
static const struct {
    uint32_t va;
    const unsigned char *bytes;
    size_t size;
    const unsigned short *addresses;
    size_t address_count;
    void (*lift)(void);
} cases[] = { CASES };

enum { data_start = 0x2C0000, data_end = 0x580000, guest_stack = 0x5A0800 };
static unsigned char expected[data_end - data_start];
static const uint32_t dependencies[] = { DEPENDENCY_ADDRESSES };

static void setup(unsigned int variant, uint32_t bias)
{
    memset((void *)(g_xbox_mem_offset + data_start), 0, sizeof(expected));
    MEM32(0x355740) = 0x500000 + bias;
    MEM32(0x351F68) = 0x510000 + bias;
    MEM32(0x4CA87C) = 0x520000 + bias;
    MEM32(0x35285C) = variant;
    MEM32(0x357688) = variant;
    MEM32(0x3573D4) = variant % 3;
    MEM16(0x355758) = (uint16_t)(variant + 1);
    MEM32(0x56161C) = 1;
    for (unsigned int index = 0; index < 3; ++index) {
        MEM16(0x500012 + index * 2) = (uint16_t)(index + 1);
        MEM16(0x50001E + index * 2) = (uint16_t)(index + 4);
        MEM16(0x50002A + index * 2) = (uint16_t)(index + variant);
    }
}

int main(void)
{
    const uint32_t first = 0x60000;
    unsigned char *ram = VirtualAlloc(NULL, 0x5B0000 - first,
                                     MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    uint32_t bias;
    DWORD protection;
    if (!ram) {
        fprintf(stderr, "Cannot allocate the fixture (%lu)\n", GetLastError());
        return 2;
    }
    bias = (uint32_t)(uintptr_t)ram - first;
    g_xbox_mem_offset = bias;
    for (unsigned int i = 0; i < _countof(dependencies); ++i)
        *(unsigned char *)(bias + dependencies[i]) = 0xC3; /* isolated cdecl return */
    for (unsigned int i = 0; i < _countof(cases); ++i) {
        unsigned char *original = (unsigned char *)(bias + cases[i].va);
        memcpy(original, cases[i].bytes, cases[i].size);
        for (unsigned int j = 0; j < cases[i].address_count; ++j) {
            uint32_t address;
            unsigned char *field = original + cases[i].addresses[j];
            memcpy(&address, field, sizeof(address));
            address += bias;
            memcpy(field, &address, sizeof(address));
        }
    }
    if (!VirtualProtect(ram, 0xC0000 - first, PAGE_EXECUTE_READ, &protection) ||
        !FlushInstructionCache(GetCurrentProcess(), ram, 0xC0000 - first)) {
        fprintf(stderr, "Cannot prepare fixture code (%lu)\n", GetLastError());
        return 2;
    }
    for (unsigned int i = 0; i < _countof(cases); ++i) {
        void (*native)(void) = (void (*)(void))(bias + cases[i].va);
        for (unsigned int vector = 0; vector < 4; ++vector) {
            setup(vector, bias);
            native();
            memcpy(expected, (const void *)(bias + data_start), sizeof(expected));
            for (size_t j = 0; j < sizeof(expected); j += sizeof(uint32_t)) {
                uint32_t word;
                memcpy(&word, expected + j, sizeof(word));
                if (word - bias >= data_start && word - bias < data_end) {
                    word -= bias;
                    memcpy(expected + j, &word, sizeof(word));
                }
            }
            setup(vector, 0);
            g_eax = g_ebx = g_ecx = g_edx = g_esi = g_edi = g_ebp = g_seh_ebp = 0;
            g_ebx = 0x12345678;
            g_esi = 0x23456789;
            g_edi = 0x3456789A;
            g_esp = guest_stack;
            MEM32(guest_stack) = 0xABCDEF12;
            cases[i].lift();
            if (g_esp != guest_stack + 4 || g_ebx != 0x12345678 ||
                g_esi != 0x23456789 || g_edi != 0x3456789A ||
                memcmp(expected, (const void *)(bias + data_start), sizeof(expected))) {
                size_t j;
                const unsigned char *actual = (const unsigned char *)(bias + data_start);
                for (j = 0; j < sizeof(expected) && actual[j] == expected[j]; ++j) {}
                fprintf(stderr, "%08X vector %u differs from x86 (guest SP=%08X, first data difference=%08X)\n",
                        cases[i].va, vector, g_esp, (unsigned int)(data_start + j));
                return 1;
            }
        }
    }
    puts("Mission state-table callbacks match original x86 in 12 cases");
    return 0;
}
"""

symbols = set(re.findall(r"\bsub_([0-9A-F]{8})\b", "\n".join(bodies.values())))
external = sorted(int(addr, 16) for addr in symbols if int(addr, 16) not in bodies)
dependency_impl = "\n".join(f"static void sub_{addr:08X}(void) {{ g_esp += 4; }}"
                            for addr in external)
decoder = seed["Cs"](seed["CS_ARCH_X86"], seed["CS_MODE_32"])
decoder.detail = True
arrays = []
for address, original in originals.items():
    offsets = set()
    for i in decoder.disasm(original, address):
        for operand in i.operands:
            if (operand.type == X86_OP_MEM and i.disp_size == 4
                    and 0x2C0000 <= operand.mem.disp < 0x580000):
                offsets.add(i.address - address + i.disp_offset)
            if (operand.type == X86_OP_IMM and i.imm_size == 4
                    and 0x2C0000 <= operand.imm < 0x580000):
                offsets.add(i.address - address + i.imm_offset)
    arrays.append(f"static const unsigned char bytes_{address:08X}[] = {{ " +
                  ", ".join(f"0x{byte:02X}" for byte in original) + " };")
    arrays.append(f"static const unsigned short reloc_{address:08X}[] = {{ " +
                  ", ".join(map(str, sorted(offsets))) + " };")
cases = ",\n".join(f"{{ 0x{a:08X}, bytes_{a:08X}, sizeof(bytes_{a:08X}), "
                   f"reloc_{a:08X}, _countof(reloc_{a:08X}), sub_{a:08X} }}"
                   for a in bodies)
with tempfile.TemporaryDirectory() as directory:
    work = Path(directory)
    source = work / "mission_dispatch.c"
    source.write_text(HARNESS.replace("DEPENDENCY_ADDRESSES", ", ".join(map(hex, external)))
                      .replace("DEPENDENCIES", dependency_impl)
                      .replace("LIFTED", "\n".join(bodies.values()))
                      .replace("ORIGINALS", "\n".join(arrays)).replace("CASES", cases),
                      encoding="utf-8")
    output = work / "callbacks.exe"
    subprocess.run([os.environ.get("CC", "cl.exe"), "/nologo", "/TC", "/Od",
                    "/Oy-", "/I" + str(generation), str(source), "/Fe:" + str(output),
                    "/link", "/MANIFESTUAC:level='asInvoker'"],
                   cwd=work, check=True)
    subprocess.run([str(output)], cwd=work, check=True, timeout=15)
