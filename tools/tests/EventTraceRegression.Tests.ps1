$ErrorActionPreference='Stop'
$env:QUASAR_VC64=Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
@'
from pathlib import Path
import xml.etree.ElementTree as ET,subprocess,tempfile,os
root=Path.cwd()
recomp=root/"local-tools/xboxrecomp"
build=root/"local-tools/steel-battalion-game/build-xpp-seeded"
ns={'m':'http://schemas.microsoft.com/developer/msbuild/2003'}
tree=ET.parse(build/"your_game_recomp.vcxproj")
grp=next(g for g in tree.findall('.//m:ItemDefinitionGroup',ns) if "'Release|x64'" in g.get('Condition',''))
deps=grp.find('m:Link/m:AdditionalDependencies',ns).text.split(';')
libs=' '.join('"'+str(build/d)+'"' if '\\' in d else d for d in deps if not d.startswith('%('))
with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
 p=Path(tmp)
 vc=os.environ['QUASAR_VC64']
 source=recomp/"tests/memory_regressions/test_main.c"
 cmd=f'"{vc}" >nul && cl /nologo /TC /MD /O2 /Gy /D_CRT_SECURE_NO_WARNINGS /I"{recomp/"src"}" /I"{recomp/"src/kernel"}" "{source}" "{recomp/"src/kernel/kernel_bridge.c"}" /Fe:test.exe /link /OPT:REF {libs}'
 result=subprocess.run(cmd,cwd=p,shell=True,capture_output=True,text=True)
 print("\n".join(l for l in result.stdout.splitlines() if "error" in l.lower()),flush=True)
 if result.returncode:raise SystemExit(result.returncode)
 failed=False
 for mode in ("event-observer","event-observer-trace"):
  result=subprocess.run([str(p/"test.exe"),mode,str(recomp/"tools/conformance/test.xbe"),str(p/"wait.log")],cwd=p,timeout=30,capture_output=True,text=True)
  failed=failed or result.returncode!=0
  print(mode,'EXIT',result.returncode,flush=True)
  print(result.stdout,flush=True)
  if result.returncode:print(result.stderr[-1500:],flush=True)
 if failed:raise SystemExit(1)
 print("fixture done")
'@ | python -
exit $LASTEXITCODE
