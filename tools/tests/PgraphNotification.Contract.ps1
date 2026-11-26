$ErrorActionPreference='Stop'
$env:QUASAR_VC64=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
@'
from pathlib import Path
import os,subprocess,tempfile
root=Path.cwd()/"local-tools/xboxrecomp/src/nv2a"
with tempfile.TemporaryDirectory() as tmp:
 p=Path(tmp)
 (p/"test.c").write_text(r"""
#include "nv2a_core.c"
int pgraph_d3d11_method(int subchannel,uint32_t method,uint32_t param) {return 0;}
static HANDLE asserted;
static void irq(int on) { if(on)SetEvent(asserted); }
static DWORD WINAPI producer(void *param) {
 nv2a_pgraph_notify(0,0x100,(uint32_t)(uintptr_t)param);
 return 0;
}
int main(void) {
 HANDLE t; uint32_t seen;
 g_nv2a=calloc(1,sizeof(*g_nv2a));
 qemu_mutex_init(&g_nv2a->pgraph.lock);
 qemu_cond_init(&g_nv2a->pgraph.interrupt_cond);
 g_nv2a->pgraph.enabled_interrupts=NV_PGRAPH_INTR_ERROR;
 g_nv2a->pmc.enabled_interrupts=NV_PMC_INTR_EN_0_HARDWARE;
 asserted=CreateEventA(0,0,0,0);
 nv2a_set_irq_line_callback(irq);
 for(unsigned cycle=0;cycle<256;++cycle) {
 t=CreateThread(0,0,producer,(void*)5,0,0);
 if(WaitForSingleObject(asserted,2000)!=WAIT_OBJECT_0)return 2;
 seen=pgraph_read(g_nv2a,NV_PGRAPH_TRAPPED_DATA_LOW,4);
 if(seen!=5 || WaitForSingleObject(t,0)!=WAIT_TIMEOUT)return 3;
 pgraph_write(g_nv2a,NV_PGRAPH_INTR,NV_PGRAPH_INTR_ERROR,4);
 if(WaitForSingleObject(t,2000)!=WAIT_OBJECT_0)return 4;

 CloseHandle(t);
 }
 nv2a_pgraph_notify(0,0x100,0);
 if(pgraph_read(g_nv2a,NV_PGRAPH_INTR,4)&NV_PGRAPH_INTR_ERROR)return 5;
 puts("PASS 256 real payload-5 acknowledgement cycles and zero no-op");
 return 0;
}
""")
 vc=os.environ["QUASAR_VC64"]
 subprocess.run(f'"{vc}" >nul && cl /nologo /TC /O2 /Gy /D_CRT_SECURE_NO_WARNINGS /I"{root}" /I"{root.parent}" test.c /Fe:test.exe /link /OPT:REF',cwd=p,shell=True,check=True)
 subprocess.run([str(p/"test.exe")],cwd=p,timeout=10,check=True)
'@ | python -
exit $LASTEXITCODE
