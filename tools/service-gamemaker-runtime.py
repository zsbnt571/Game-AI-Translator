"""Service reviewed GameMaker CLI runtime assets in isolated public copies only.

Dry-run is the default. Does not build business code, download, execute payloads,
modify the installed Fusion application, or upload. Requires the original
byte-match evidence and the exact official NuGet runtime package below.
"""
from pathlib import Path
import argparse,copy,datetime,hashlib,json,os,shutil,zipfile
import xml.etree.ElementTree as ET

PACKAGE='Microsoft.NETCore.App.Runtime.win-x64'
OLD='10.0.11';NEW='10.0.14'
PACKAGE_SHA='A3EA61B4A2DA6D28E562DD1CDACF80EB4DD6B9727B0A2B47F9D6ABA90DBBCB1F'
TARGET='.NETCoreApp,Version=v10.0/win-x64'
PREFIX='runtimepack.'+PACKAGE
OLD_DAC='mscordaccore_amd64_amd64_10.0.1126.37416.dll'
NEW_DAC='mscordaccore_amd64_amd64_10.0.1426.52326.dll'
def digest(b):return hashlib.sha256(b).hexdigest().upper()
def filehash(p):return digest(p.read_bytes())
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def encode(v):return (json.dumps(v,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def fail_if(condition,message):
    if condition:raise RuntimeError(message)
def confined(base,relative):
    p=(base/relative).resolve()
    fail_if(not p.is_relative_to(base.resolve()),'Path escapes specified root')
    return p
def normalized_deps(d):
    d=copy.deepcopy(d)
    for key in list(d['targets'][TARGET]):
        if key.startswith(PREFIX+'/'):del d['targets'][TARGET][key]
        else:d['targets'][TARGET][key].get('dependencies',{}).pop(PREFIX,None)
    for key in list(d['libraries']):
        if key.startswith(PREFIX+'/'):del d['libraries'][key]
    return d
def normalized_config(d):
    d=copy.deepcopy(d)
    for f in d['runtimeOptions']['includedFrameworks']:
        if f['name']=='Microsoft.NETCore.App':f['version']='<runtime-version>'
    return d

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    for n in ('source-root','binary-root','package','old-evidence','audit-dir'):ap.add_argument('--'+n,type=Path,required=True)
    ap.add_argument('--apply',action='store_true');a=ap.parse_args()
    roots={'source':a.source_root.resolve(),'binary':a.binary_root.resolve()};audit=a.audit_dir.resolve()
    fail_if(roots['source']==roots['binary'],'Roots must differ')
    for root in roots.values():
        fail_if(not root.is_dir(),'Missing public work root')
        fail_if(audit.is_relative_to(root) or root.is_relative_to(audit),'Audit backup root must be separate from public roots')
        fail_if((root/'settings.json').exists(),'Refusing configured application root')
        marker=read(root/'package-info.json')
        fail_if(marker.get('schema')!='Fusion.PublicCandidate.v1' or marker.get('fileVersion')!='1.1.0.127' or marker.get('includesPersonalConfiguration') is not False,'Expected clean public candidate marker')
        if (root/'data-location.json').exists():fail_if(read(root/'data-location.json')!={'Path':'data'},'Expected clean relative data-location default')
        fail_if((root/'data').exists() and any(p.is_file() for p in (root/'data').rglob('*')),'Refusing populated application data directory')
    fail_if(filehash(a.package)!=PACKAGE_SHA,'Official runtime package SHA mismatch')
    evidence=read(a.old_evidence);old_hashes={};all_expected={Path(row['path']).name:row['sha256'] for row in evidence}
    fail_if(len(all_expected)!=202,'Expected original 202-binary GameMaker evidence')
    for row in evidence:
        if any(m['component']==PACKAGE+'/'+OLD for m in row['packageMatches']):
            name=Path(row['path']).name;fail_if(name in old_hashes,'Duplicate old runtime basename');old_hashes[name]=row['sha256']
    fail_if(len(old_hashes)!=187,'Expected 187 exact old runtime asset hashes')
    with zipfile.ZipFile(a.package) as z:
        xml_bytes=z.read('data/RuntimeList.xml');runtime=ET.fromstring(xml_bytes)
        files=[e.attrib for e in runtime.findall('File') if e.attrib.get('Type') in ('Managed','Native')]
        fail_if(len(files)!=187,'Expected 187 RuntimeList managed/native assets')
        new_files={};new_meta={'runtime':{},'native':{}};mapping=[]
        for row in files:
            name=Path(row['Path']).name
            fail_if(name in new_files,'Duplicate new runtime basename')
            b=z.read(row['Path']);new_files[name]=b
            section='runtime' if row['Type']=='Managed' else 'native'
            new_meta[section][name]={key[0].lower()+key[1:]:row[key] for key in ('AssemblyVersion','FileVersion') if key in row}
            old_name=OLD_DAC if name==NEW_DAC else name
            fail_if(old_name not in old_hashes,'New runtime asset has no reviewed baseline owner: '+name)
            mapping.append({'oldName':old_name,'oldSha256':old_hashes[old_name],'newName':name,'newSha256':digest(b),'bytes':len(b),'packageEntry':row['Path'],'type':row['Type'],'metadata':new_meta[section][name]})
        fail_if(set(old_hashes)-set(new_files)!={OLD_DAC} or set(new_files)-set(old_hashes)!={NEW_DAC},'Unexpected runtime asset rename or removal')
        fail_if(len(new_meta['runtime'])!=172 or len(new_meta['native'])!=15,'Unexpected managed/native split')
        notices={n:z.read(n) for n in ('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT',PACKAGE+'.nuspec')}
    stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    run=audit/('apply-'+stamp if a.apply else 'dry-run-'+stamp)
    plan=[];protected={};public_map={'schema':'fusion127-gamemaker-runtime-service-v1','oldRuntime':OLD,'newRuntime':NEW,'package':PACKAGE,'packageSha256':PACKAGE_SHA,'packageUrl':'https://api.nuget.org/v3-flatcontainer/'+PACKAGE.lower()+'/'+NEW+'/'+PACKAGE.lower()+'.'+NEW+'.nupkg','runtimeListSha256':digest(xml_bytes),'assetCount':187,'assets':mapping,'businessRecompiled':False,'baselineEvidence':'licenses/provenance/gamemaker-binary-exact-nuget-matches.json','note':'Only the runtimepack-owned assets and runtimepack metadata are serviced. Business assemblies, apphost and other packages remain byte-identical. The old DAC is retained in a separate local audit backup.'}
    doc=('''# GameMaker CLI runtime servicing for Fusion 1.1.0.127

The bundled UndertaleModCli business assemblies and application executable retain their reviewed upstream bytes. This release services their self-contained Microsoft .NET runtime from 10.0.11 to 10.0.14 using the original official NuGet package, without recompiling the tool's business code.

The package data/RuntimeList.xml is the ownership list: 172 managed and 15 native assets, including hostfxr. Each original asset must match the earlier exact-package evidence. The only basename change is the versioned mscordaccore DAC. The old file is moved to a separate audit backup; it is not left in the public runtime. All other files outside these assets and the application's runtimeconfig/deps runtimepack fields must retain their hashes.

The service script is `tools/service-gamemaker-runtime.py`. Its default is a dry-run. Explicit input roots, exact package and prior evidence are required; audit backups must be outside both public roots. Use it only on fresh isolated public working copies. It performs complete preflight validation before any writes, preserves old files in the audit backup and restores them if apply fails. It does not download or execute the tool or a game.

The application's deps file is updated in place semantically: only the runtimepack version key, dependency version and managed/native runtime metadata change. The package's framework deps file is not copied over the application deps file. Original Microsoft LICENSE and THIRD-PARTY-NOTICES are included in `licenses/NuGet/Microsoft.NETCore.App.Runtime.win-x64/10.0.14/`. The detailed current mapping is `licenses/provenance/GameMaker-runtime-10.0.14.json`; the 10.0.11 mapping remains historical evidence. This file documents provenance and the service operation, not a claim of original-game functional acceptance.
''').encode('utf-8')
    def schedule(scope,relative,b=None,retire=False):
        dest=confined(roots[scope],relative);before=filehash(dest) if dest.is_file() else None
        fail_if(dest.exists() and not dest.is_file(),'Non-file collision')
        plan.append({'scope':scope,'path':relative,'beforeSha256':before,'afterSha256':None if retire else digest(b),'retire':retire,'bytes':b})
    for scope,root in roots.items():
        relative='Dependencies/GameMakerTool' if scope=='source' else 'adapters/gamemaker';tool=confined(root,relative)
        fail_if(not tool.is_dir(),'Expected GameMaker directory missing')
        deps_path=tool/'UndertaleModCli.deps.json';config_path=tool/'UndertaleModCli.runtimeconfig.json'
        deps=read(deps_path);config=read(config_path)
        old_node=deps['targets'][TARGET][PREFIX+'/'+OLD]
        fail_if(set(old_node['runtime'])|set(old_node['native'])!=set(old_hashes),'Application deps runtime asset ownership differs')
        other_assets={Path(name).name for k,v in deps['targets'][TARGET].items() if k!=PREFIX+'/'+OLD for section in ('runtime','native') for name in v.get(section,{})}
        fail_if(bool(other_assets&set(new_files)),'Runtime asset overlaps a business/third-party asset')
        for name,expected in all_expected.items():fail_if(filehash(tool/name)!=expected,'Original business/third-party/runtime digest mismatch: '+scope+'/'+name)
        for name,expected in old_hashes.items():fail_if(filehash(tool/name)!=expected,'Old runtime digest mismatch: '+scope+'/'+name)
        fail_if((tool/NEW_DAC).exists(),'New DAC already exists; baseline is not fresh')
        protected[scope]={p.relative_to(tool).as_posix():filehash(p) for p in tool.rglob('*') if p.is_file() and p.name not in old_hashes and p.name not in ('UndertaleModCli.deps.json','UndertaleModCli.runtimeconfig.json')}
        fail_if(any(x not in protected[scope] for x in ('UndertaleModCli.exe','UndertaleModCli.dll','UndertaleModLib.dll','Underanalyzer.dll')),'Required protected business binaries missing')
        original_deps=copy.deepcopy(deps);original_config=copy.deepcopy(config)
        del deps['targets'][TARGET][PREFIX+'/'+OLD];deps['targets'][TARGET][PREFIX+'/'+NEW]=new_meta
        dep_references=0
        for node in deps['targets'][TARGET].values():
            if PREFIX in node.get('dependencies',{}):
                fail_if(node['dependencies'][PREFIX]!=OLD,'Unexpected previous runtime dependency');node['dependencies'][PREFIX]=NEW;dep_references+=1
        fail_if(dep_references!=1,'Expected one root runtimepack dependency')
        deps['libraries'][PREFIX+'/'+NEW]=deps['libraries'].pop(PREFIX+'/'+OLD)
        matched=0
        for f in config['runtimeOptions']['includedFrameworks']:
            if f['name']=='Microsoft.NETCore.App':
                fail_if(f['version']!=OLD,'Unexpected old runtimeconfig version');f['version']=NEW;matched+=1
        fail_if(matched!=1,'Expected one included .NET runtime')
        fail_if(normalized_deps(deps)!=normalized_deps(original_deps),'Non-runtime dependency metadata changed')
        fail_if(normalized_config(config)!=normalized_config(original_config),'Non-runtime configuration changed')
        for name,b in new_files.items():schedule(scope,relative+'/'+name,b)
        schedule(scope,relative+'/'+OLD_DAC,retire=True)
        schedule(scope,relative+'/UndertaleModCli.deps.json',encode(deps));schedule(scope,relative+'/UndertaleModCli.runtimeconfig.json',encode(config))
        license_files=[]
        for name,b in notices.items():
            rel='licenses/NuGet/'+PACKAGE+'/'+NEW+'/'+name;license_files.append(rel);schedule(scope,rel,b)
        inventory=read(root/'licenses/nuget-inventory.json');fail_if(any(x['component']==PACKAGE+'/'+NEW for x in inventory),'New runtime inventory already present')
        inventory.append({'component':PACKAGE+'/'+NEW,'files':license_files});schedule(scope,'licenses/nuget-inventory.json',encode(inventory))
        schedule(scope,'licenses/provenance/GameMaker-runtime-'+NEW+'.json',encode(public_map))
        schedule(scope,'docs/GAMEMAKER-RUNTIME-SERVICING.md',doc)
        schedule(scope,'tools/service-gamemaker-runtime.py',Path(__file__).read_bytes())
    fail_if(set(protected['source'])-set(protected['binary']),'Unexpected source-only protected files')
    fail_if(set(protected['binary'])-set(protected['source'])!={'GameMakerTranslationBridge.gml','GameMakerPatch.csx','bridge-font.otf'},'Unexpected package-only adapter files')
    fail_if(any(h!=protected['binary'][p] for p,h in protected['source'].items()),'Shared protected business/third-party files differ between the two copies')
    protected_counts={scope:len(files) for scope,files in protected.items()}
    public_plan=[{k:v for k,v in row.items() if k!='bytes'} for row in plan]
    run.mkdir(parents=True,exist_ok=False)
    (run/'plan.json').write_bytes(encode({'applyRequested':a.apply,'packageSha256':PACKAGE_SHA,'runtimeAssetsPerCopy':187,'protectedFilesPerCopy':protected_counts,'operations':public_plan,'protectedFiles':protected}))
    if not a.apply:
        print(json.dumps({'applied':False,'operations':len(plan),'runtimeAssetsPerCopy':187,'protectedFilesPerCopy':protected_counts,'receipt':str(run/'plan.json')}));return
    applied=[]
    try:
        # Recheck the entire plan before mutation to catch concurrent work.
        for row in plan:
            dest=confined(roots[row['scope']],row['path'])
            fail_if((filehash(dest) if dest.is_file() else None)!=row['beforeSha256'],'Concurrent file change: '+row['scope']+'/'+row['path'])
        for row in plan:
            dest=confined(roots[row['scope']],row['path']);backup=confined(run/'backups'/row['scope'],row['path'])
            if row['beforeSha256'] is not None:
                backup.parent.mkdir(parents=True,exist_ok=True)
                if row['retire']:
                    shutil.move(str(dest),str(backup));applied.append(row)
                else:shutil.copy2(dest,backup)
                fail_if(filehash(backup)!=row['beforeSha256'],'Backup hash mismatch')
            if not row['retire']:applied.append(row)
            if not row['retire']:
                dest.parent.mkdir(parents=True,exist_ok=True);temporary=dest.with_name(dest.name+'.fusion-runtime-service.tmp')
                fail_if(temporary.exists(),'Temporary file collision');temporary.write_bytes(row['bytes']);os.replace(temporary,dest)
                fail_if(filehash(dest)!=row['afterSha256'],'Written file hash mismatch')
        for scope,root in roots.items():
            tool=root/('Dependencies/GameMakerTool' if scope=='source' else 'adapters/gamemaker')
            for rel,h in protected[scope].items():fail_if(filehash(tool/rel)!=h,'Protected file changed: '+scope+'/'+rel)
            fail_if((tool/OLD_DAC).exists(),'Old DAC remains in public runtime')
        (run/'success.json').write_bytes(encode({'applied':True,'packageSha256':PACKAGE_SHA,'runtimeAssetsPerCopy':187,'protectedFilesPerCopy':protected_counts,'protectedFilesUnchanged':True,'oldDacMovedToBackup':True,'operations':public_plan}))
        print(json.dumps({'applied':True,'receipt':str(run/'success.json')}))
    except Exception as ex:
        errors=[]
        for row in reversed(applied):
            try:
                dest=confined(roots[row['scope']],row['path']);backup=confined(run/'backups'/row['scope'],row['path'])
                if row['beforeSha256'] is not None:
                    shutil.copy2(backup,dest);fail_if(filehash(dest)!=row['beforeSha256'],'Rollback digest mismatch')
                elif dest.is_file():
                    fail_if(filehash(dest)!=row['afterSha256'],'Unexpected file during rollback');dest.unlink()
                temporary=dest.with_name(dest.name+'.fusion-runtime-service.tmp')
                if temporary.is_file() and row['afterSha256'] is not None:
                    fail_if(filehash(temporary)!=row['afterSha256'],'Unexpected temporary file during rollback');temporary.unlink()
            except Exception as rollback_error:errors.append(str(rollback_error))
        (run/'failure.json').write_bytes(encode({'applied':False,'error':str(ex),'rollbackErrors':errors,'originalBackupsRetained':True}))
        raise
if __name__=='__main__':main()
