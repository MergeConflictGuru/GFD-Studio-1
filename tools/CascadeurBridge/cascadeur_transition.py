"""Build a Cascadeur AI transition from two Persona GAP clips.
Requires Cascadeur's Scripts > MCP > Start script server, with playback paused.
"""
import argparse
import http.client
import json
import subprocess
from pathlib import Path
import sys
import time
import uuid
import hashlib
import shutil

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_DIR = Path(__file__).resolve().parent
import os
CASCADEUR = Path(os.environ.get('CASCADEUR_HOME', 'Q:/_coding/tools/Cascadeur'))
DEFAULT_BRIDGE = Path('Q:/_coding/GFD-Studio/GFDStudio/bin/x64/Release/net8.0-windows/win-x64/RetargetProbe.exe')

def app_running():
    result=subprocess.run(['tasklist','/FI','IMAGENAME eq cascadeur.exe','/FO','CSV'],capture_output=True,text=True)
    return 'cascadeur.exe' in result.stdout.lower()

def rig_cache(model, bridge_exe):
    """A rig belongs to the model bytes and the tools which created it."""
    digest = hashlib.sha256(b'cascadeur-rig-cache-v1')
    files = [model, SCRIPT_DIR/'cascadeur_transition_scene.py',
             CASCADEUR/'resources/autorig_templates/UE4_New.qrigcasc',
             bridge_exe.parent/'GFDLibrary.dll',
             bridge_exe.parent/'GFDLibrary.Conversion.FbxSdk.dll',
             CASCADEUR/'cascadeur.exe']
    for file in files:
        with file.open('rb') as stream:
            while block := stream.read(1024*1024):
                digest.update(block)
    return ROOT/'tmp/cascadeur_rig_cache'/digest.hexdigest()

def bridge(exe, stage, job):
    result = subprocess.run([str(exe), '--cascadeur-job', stage, str(job)],
                            capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(), flush=True)

def cascadeur(stage, manifest, server):
    module = SCRIPT_DIR/'cascadeur_transition_scene.py'
    receipt = ROOT/'tmp'/f'cascadeur_receipt_{uuid.uuid4().hex}.json'
    receipt.parent.mkdir(exist_ok=True)
    code = ("import runpy, json, traceback, os\n"
            f"os.environ['CASCADEUR_HOME'] = {str(CASCADEUR)!r}\n"
            f"_transition = runpy.run_path({str(module)!r})\n"
            "try:\n"
            f"    _transition[{stage!r}]({str(manifest)!r}{', scene' if stage == 'close_generated' else ''})\n"
            f"    open({str(receipt)!r},'w').write(json.dumps({{'ok':True}}))\n"
            "except Exception:\n"
            f"    open({str(receipt)!r},'w').write(json.dumps({{'ok':False,'error':traceback.format_exc()}}))\n"
            "    raise\n")
    host, port = server.rsplit(':',1)
    print('Cascadeur:',stage,flush=True)
    connection = http.client.HTTPConnection(host, int(port), timeout=35)
    connection.request('POST','/run',json.dumps({'code':code}),{'Content-Type':'application/json'})
    try:
        result = json.loads(connection.getresponse().read())
    except (TimeoutError, ConnectionError, json.JSONDecodeError):
        result = {'ok':False,'error':'Timed out waiting for the script reply'}
    finally:
        connection.close()
    if not result.get('ok') and 'Timed out waiting' in result.get('error',''):
        # The app keeps running a script after its server's 30-second reply.
        # Wait for our receipt rather than submitting the same script twice.
        deadline=time.monotonic()+300
        while not receipt.exists() and time.monotonic()<deadline:
            if not app_running():
                raise ConnectionError('Cascadeur closed while running '+stage)
            time.sleep(1)
        if not receipt.exists():
            raise RuntimeError('Cascadeur did not finish '+stage+' within five minutes')
        result=json.loads(receipt.read_text())
        print('Cascadeur finished:',stage,flush=True)
    (ROOT/'tmp').mkdir(exist_ok=True)
    (ROOT/'tmp'/f'cascadeur_transition_{stage}.json').write_text(json.dumps(result,indent=2))
    if not result.get('ok'):
        raise RuntimeError(result.get('error','Cascadeur stage failed'))
    errors = [m['text'] for m in result.get('messages',[]) if m.get('level')=='Error']
    if errors:
        raise RuntimeError('\n'.join(errors))
    info = [m['text'] for m in result.get('messages',[]) if m.get('level')=='Info']
    print('\n'.join(info[-2:]), flush=True)

def ensure_server(cascadeur_dir, server, initial_file):
    # Installing this command makes later Cascadeur launches start the bridge.
    plugin = cascadeur_dir/'resources/scripts/python/scripts/gfd_ai_bridge.py'
    source = SCRIPT_DIR/'gfd_ai_bridge.py'
    if not plugin.exists() or plugin.read_bytes() != source.read_bytes():
        plugin.write_bytes(source.read_bytes())
    host, port = server.rsplit(':',1)
    def ready():
        connection = http.client.HTTPConnection(host,int(port),timeout=2)
        try:
            connection.request('GET','/health')
            return json.loads(connection.getresponse().read()).get('ok',False)
        except (OSError,ValueError):return False
        finally:connection.close()
    if ready():return
    # Do not start a second app over somebody's open scenes.
    if app_running():
        # The app may still be loading a scene before its first idle callback.
        deadline=time.monotonic()+15
        while time.monotonic()<deadline:
            if ready():return
            time.sleep(1)
        raise RuntimeError('In Cascadeur choose Settings > Reload scripts once, or Scripts > MCP > Start script server. Keep playback paused.')
    startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
    subprocess.Popen([str(cascadeur_dir/'cascadeur.exe'),str(initial_file)],cwd=cascadeur_dir,startupinfo=startup)
    deadline=time.monotonic()+150
    while time.monotonic()<deadline:
        if ready():return
        time.sleep(1)
    raise RuntimeError('Cascadeur opened but its bridge is not ready. Open a scene and keep playback paused.')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cascadeur',type=Path,default=CASCADEUR)
    parser.add_argument('stage', choices=['run','prepare','rig','interpolate','finish','preview'])
    parser.add_argument('job',type=Path)
    parser.add_argument('--bridge',type=Path,default=DEFAULT_BRIDGE)
    parser.add_argument('--server',default='127.0.0.1:8765')
    parser.add_argument('--preview',action='store_true',help='Open the returned GAP mesh in Cascadeur after writing it')
    parser.add_argument('--rebuild-rig',action='store_true',help='Build a new rig instead of loading the model cache')
    args = parser.parse_args()
    globals()['CASCADEUR'] = args.cascadeur.resolve()
    started = time.monotonic()
    timings = {}
    def stage(name, function, *values):
        before = time.monotonic()
        try:
            result = function(*values)
        except (OSError,RuntimeError) as error:
            # A saved rig can outlive a Cascadeur crash after cold rigging.
            # Reopen that scene only when the app has actually closed. Do not
            # resubmit a timed-out script to an app which is still running.
            scene_file=output/'transition.casc'
            if function != cascadeur or app_running() or not scene_file.is_file() or name not in ['import_animation','ai','bake','preview']:
                raise
            print('Cascadeur closed; reopening the saved scene',flush=True)
            ensure_server(args.cascadeur.resolve(),args.server,scene_file)
            result = function(*values)
        timings[name] = round(time.monotonic()-before,3)
        return result
    job = args.job.resolve()
    config = json.loads(job.read_text())
    output = Path(config.get('output','output'))
    if not output.is_absolute():
        output = job.parent/output
    manifest = output/'manifest.json'
    if args.stage in ['run','prepare']:
        stage('prepare',bridge,args.bridge,'prepare',job)
    if args.stage != 'prepare':
        prepared = json.loads(manifest.read_text())
        if prepared.get('jobHash') != hashlib.sha256(job.read_bytes()).hexdigest().upper():
            raise RuntimeError('Job settings changed; prepare and rig again before using this scene')
    if args.stage in ['run','rig','interpolate','finish','preview']:
        ensure_server(args.cascadeur.resolve(),args.server,output/'rig.fbx')
    if args.stage in ['run','rig']:
        cache = rig_cache(Path(prepared['model']),args.bridge.resolve())
        reused = not args.rebuild_rig and (cache/'rig.casc').is_file() and (cache/'interchange.json').is_file()
        if reused:
            prepared['rigCache'] = str(cache)
            manifest.write_text(json.dumps(prepared,separators=(',',':')))
            stage('load_cached_rig',cascadeur,'load_cached',manifest,args.server)
        else:
            stage('load_model',cascadeur,'load',manifest,args.server)
            stage('generate_rig',cascadeur,'rig',manifest,args.server)
        # Refuse to report AI when the saved scene has no AI core.
        # The AI stage refuses rigs without AutoPosing tracks.
        if not reused:
            cache.mkdir(parents=True,exist_ok=True)
            shutil.copy2(output/'interchange.json',cache/'interchange.json')
            shutil.copy2(output/'transition.casc',cache/'rig.part')
            (cache/'rig.part').replace(cache/'rig.casc')
            print('Saved model rig for later clips',flush=True)
        stage('import_animation',cascadeur,'animation',manifest,args.server)
        stage('close_older_scenes',cascadeur,'close_generated',manifest,args.server)
    if args.stage in ['run','interpolate']:
        stage('ai',cascadeur,'interpolate',manifest,args.server)
        stage('reconnect_ai',cascadeur,'reconnect_ai',manifest,args.server)
        stage('close_older_scenes',cascadeur,'close_generated',manifest,args.server)
    if args.stage in ['run','finish']:
        stage('bake',cascadeur,'bake',manifest,args.server)
        stage('write_gap',bridge,args.bridge,'finish',job)
        print('GAP output:',output,flush=True)
    if args.stage == 'preview' or (args.preview and args.stage in ['run','finish']):
        stage('preview',cascadeur,'preview',manifest,args.server)
        stage('close_older_scenes',cascadeur,'close_generated',manifest,args.server)
    elif args.stage in ['run','finish']:
        stage('leave_job_scene',cascadeur,'release',manifest,args.server)
        stage('close_job_scenes',cascadeur,'close_generated',manifest,args.server)
    timings['total'] = round(time.monotonic()-started,3)
    (output/'timings.json').write_text(json.dumps(timings,indent=2))
    print('Time:',timings['total'],'seconds',flush=True)

if __name__=='__main__':
    try:
        main()
    except (OSError,RuntimeError,ValueError,KeyError) as error:
        print('Transition stopped:',error,file=sys.stderr)
        sys.exit(1)
