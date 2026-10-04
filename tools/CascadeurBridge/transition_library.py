"""Build a transition library from any dance pack into any destination animation.

The first N diverse motion samples receive new Cascadeur AI middles. The next M
samples only borrow a generated middle; their entry snap is intentionally visible.
"""
import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GFD = Path('Q:/_coding/GFD-Studio')
DEFAULT_HELPER = Path(__file__).with_name('cascadeur_transition.py')
DEFAULT_BRIDGE = GFD/'GFDStudio/bin/x64/Release/net8.0-windows/win-x64/RetargetProbe.exe'


def bridge(exe, mode, file):
    subprocess.run([str(exe), '--cascadeur-job', mode, str(file)], check=True)


def tool_fingerprint(helper, bridge_exe):
    digest = hashlib.sha256(b'transition-library-v4')
    files = [helper, helper.parent/'cascadeur_transition_scene.py',
             helper.parent/'transition_collision_report.py',
             helper.parent/'transition_motion_guard.py',
             bridge_exe.parent/'RetargetProbe.dll', bridge_exe.parent/'GFDLibrary.dll',
             bridge_exe.parent/'GFDLibrary.Conversion.FbxSdk.dll']
    for file in files:
        with file.open('rb') as stream:
            while block := stream.read(1024*1024):
                digest.update(block)
    return digest.digest()


def receipt_hash(settings, fingerprint):
    return hashlib.sha256(fingerprint+settings.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--job', type=Path, help='JSON with model, clipA, clipB and library settings; filenames relative to this JSON')
    parser.add_argument('--model', type=Path)
    parser.add_argument('--dance', type=Path)
    parser.add_argument('--dance-layer', type=Path, action='append', default=[])
    parser.add_argument('--dance-clip', type=int, default=-2, help='-2: all dance clips; -1: longest')
    parser.add_argument('--destination', type=Path)
    parser.add_argument('--destination-layer', type=Path, action='append', default=[])
    parser.add_argument('--destination-clip', type=int, default=3)
    parser.add_argument('--count', type=int, default=33)
    parser.add_argument('--reuse-count', type=int, default=33)
    parser.add_argument('--step', type=int, default=4, help='Sample every N frames at 30 fps')
    parser.add_argument('--min-frames', type=int, default=18)
    parser.add_argument('--max-frames', type=int, default=48)
    parser.add_argument('--fixed-frames', type=int, help='Disable variable duration and use N interior frames')
    parser.add_argument('--max-b-trim-percent', type=float, default=45)
    parser.add_argument('--lead-in-frames', type=int, default=30)
    parser.add_argument('--style', default='')
    parser.add_argument('--match-up', action=argparse.BooleanOptionalAction, default=False, help='Also match B root height; off by default so B keeps its ground-relative height')
    parser.add_argument('--yaw-jitter-degrees', type=float, default=0, help='Maximum random facing offset in degrees, sampled once per join')
    parser.add_argument('--yaw-jitter-seed', type=int, default=0, help='Repeatable seed for yaw variation')
    parser.add_argument('--no-collision-solving', action='store_true', help='Keep the AI middle without collision correction')
    parser.add_argument('--collision-max-correction-percent', type=float, default=5)
    parser.add_argument('--collision-max-acceleration-percent', type=float, default=.75)
    parser.add_argument('--collision-stiffness', type=float, default=15)
    parser.add_argument('--collision-passes', type=int, default=3, help='Maximum cleaning attempts, 1..8; stop earlier when head/neck clearance passes')
    parser.add_argument('--collision-padding-percent', type=float, default=0, help='Maximum extra collider clearance on later cleaning attempts; off by default')
    parser.add_argument('--output', type=Path, default=ROOT/'tmp/transitions/ann007_library33_v2')
    parser.add_argument('--helper', type=Path, default=DEFAULT_HELPER)
    parser.add_argument('--bridge', type=Path, default=DEFAULT_BRIDGE)
    parser.add_argument('--select-only', action='store_true')
    parser.add_argument('--pack-only', action='store_true')
    args = parser.parse_args()
    output=args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    if args.job:
        base=json.loads(args.job.read_text()); parent=args.job.resolve().parent
        base['model']=str((parent/Path(base['model'])).resolve())
        for name in ('clipA','clipB'):
            base[name]['pack']=str((parent/Path(base[name]['pack'])).resolve())
            base[name]['layers']=[str((parent/Path(p)).resolve()) for p in base[name].get('layers',[])]
        output=Path(base.get('output',str(output)))
        if not output.is_absolute():output=(parent/output).resolve()
        output.mkdir(parents=True,exist_ok=True)
        # Placement/collision experiments can override the example JSON without
        # editing it. Other library settings still come from that JSON.
        supplied=lambda flag:any(v==flag or v.startswith(flag+'=') for v in sys.argv[1:])
        for flag,key,value in [('--yaw-jitter-degrees','yawJitterDegrees',args.yaw_jitter_degrees),
                               ('--yaw-jitter-seed','yawJitterSeed',args.yaw_jitter_seed)]:
            if supplied(flag):base.setdefault('placement',{})[key]=value
        if supplied('--match-up') or supplied('--no-match-up'):
            base.setdefault('placement',{})['matchUp']=args.match_up
        if supplied('--no-collision-solving'):base['cleanCollisions']=False
        for flag,key,value in [('--collision-max-correction-percent','collisionMaxCorrectionPercent',args.collision_max_correction_percent),
                               ('--collision-max-acceleration-percent','collisionMaxAccelerationPercent',args.collision_max_acceleration_percent),
                               ('--collision-padding-percent','collisionMaxPaddingPercent',args.collision_padding_percent),
                               ('--collision-passes','collisionPasses',args.collision_passes)]:
            if supplied(flag):base[key]=value
        if supplied('--output'):output=args.output.resolve();output.mkdir(parents=True,exist_ok=True)
    else:
        if not all((args.model,args.dance,args.destination)):
            parser.error('Supply --job or --model, --dance and --destination')
        base={'model':str(args.model.resolve()),
              'clipA':{'pack':str(args.dance.resolve()),'index':args.dance_clip,'layers':[str(p.resolve()) for p in args.dance_layer]},
              'clipB':{'pack':str(args.destination.resolve()),'index':args.destination_clip,'layers':[str(p.resolve()) for p in args.destination_layer]},
              'poseCount':args.count,'reusePoseCount':args.reuse_count,'sampleStep':args.step,
              'minTransitionFrames':args.min_frames,'maxTransitionFrames':args.max_frames,
              'variableDuration':args.fixed_frames is None,'transitionFrames':args.fixed_frames or args.min_frames,
              'maxBTrimPercent':args.max_b_trim_percent,'leadInFrames':args.lead_in_frames,
              'motion':args.style,'cleanCollisions':not args.no_collision_solving,'collisionMuscleStiffness':args.collision_stiffness,'collisionPasses':args.collision_passes,'collisionMaxPaddingPercent':args.collision_padding_percent,
              'collisionMaxCorrectionPercent':args.collision_max_correction_percent,'collisionMaxAccelerationPercent':args.collision_max_acceleration_percent,
              'placement':{'matchPosition':True,'matchYaw':True,'matchUp':args.match_up,'yawJitterDegrees':args.yaw_jitter_degrees,'yawJitterSeed':args.yaw_jitter_seed}}
    base['output']=str(output)
    library=output/'library_job.json'; text=json.dumps(base,indent=2)
    if library.exists() and library.read_text()!=text and not args.select_only:
        parser.error('Library settings changed: use a different output folder to keep older results')
    library.write_text(text)
    if not args.pack_only:
        bridge(args.bridge,'select-library',library)
        if args.select_only:return
        plan=json.loads((output/'poses.json').read_text())
        fingerprint=tool_fingerprint(args.helper,args.bridge)
        for sample in plan['selectedSamples'][:plan['generatedCount']]:
            folder=output/sample['folder'];job=json.loads(json.dumps(base))
            job['clipA'].update(index=sample['sourceClip'],startFrame=max(0,sample['frame']-base.get('leadInFrames',30)),endFrame=sample['frame'])
            job['clipB']['startFrame']=sample['bStartFrame']
            job['transitionFrames']=sample['transitionFrames'];job['output']=str(folder)
            settings=output/(sample['folder']+'.json');settings.write_text(json.dumps(job,indent=2))
            digest=receipt_hash(settings,fingerprint)
            receipt=folder/'library_receipt.json'
            done=receipt.exists() and json.loads(receipt.read_text()).get('hash')==digest and (folder/'between.GAP').exists()
            print(f"Pose {sample['rank']}/{plan['generatedCount']}: clip {sample['sourceClip']}, frame {sample['frame']}, B frame {sample['bStartFrame']}, middle {sample['transitionFrames']} frames",flush=True)
            if not done:
                subprocess.run(['python',str(args.helper),'run',str(settings),'--bridge',str(args.bridge)],check=True)
                receipt.write_text(json.dumps({'hash':digest}))
    bridge(args.bridge,'pack-library',library)
    print('Generated middles:',output/'transitions.GAP',flush=True)
    print('New-transition demos:',output/'generated_demos.GAP',flush=True)
    print('Borrowed-transition demos:',output/'reuse_demos.GAP',flush=True)


if __name__=='__main__':main()
