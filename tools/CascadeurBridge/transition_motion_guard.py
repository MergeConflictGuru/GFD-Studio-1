"""Reject collision corrections that move unrelated joints or add sharp motion."""
import json
from pathlib import Path
import numpy as np


def report(folder):
    folder=Path(folder)
    m=json.loads((folder/'manifest.json').read_text())
    for name,default in [('collisionMaxCorrectionPercent',5),('collisionMaxAccelerationPercent',.75)]:
        value=float(m.get(name,default))
        if not np.isfinite(value) or not 0<value<=100:
            raise ValueError(name+' must be a finite percentage in (0,100]')
    before=json.loads((folder/'uncleaned_baked.json').read_text())['frames']
    after=json.loads((folder/'baked.json').read_text())['frames']
    if len(before)!=len(after):
        raise ValueError('Collision comparison needs the same frames on both sides')
    bones=[b for b in m['bones'] if b['humanoid'] and b['exported']]
    bind=np.asarray([b['bindWorld'][12:15] for b in bones])
    height=max(1.,float(np.ptp(bind[:,1])))
    allowed_sides=set(m.get('collisionAffectedSides', []))
    reasons=[];changes=[]
    for bone in bones:
        name=bone['name'];export=bone['exportName'].lower()
        if any(name not in f for f in before+after):
            raise ValueError('Collision comparison lost '+name)
        a=np.asarray([f[name] for f in before]).reshape(-1,4,4)
        b=np.asarray([f[name] for f in after]).reshape(-1,4,4)
        if not np.isfinite(a).all() or not np.isfinite(b).all():
            raise ValueError('Collision comparison has invalid joint coordinates: '+name)
        difference=b[:,3,:3]-a[:,3,:3]
        shift=float(np.linalg.norm(difference,axis=1).max())/height*100
        acceleration=float(np.linalg.norm(np.diff(difference,n=2,axis=0),axis=1).max(initial=0))/height*100
        side=export.rsplit('_',1)[-1]
        arm=side in allowed_sides and export.startswith(('clavicle_','upperarm_','lowerarm_','hand_','thumb_','index_','middle_','ring_','pinky_'))
        angle=0.
        if not arm:
            for raw,corrected in zip(a[:,:3,:3],b[:,:3,:3]):
                u,_,v=np.linalg.svd(raw);r0=u@v
                u,_,v=np.linalg.svd(corrected);r1=u@v
                angle=max(angle,float(np.degrees(np.arccos(np.clip((np.trace(r0.T@r1)-1)/2,-1,1)))))
        if shift>float(m.get('collisionMaxCorrectionPercent',5)):
            reasons.append(name+': correction is too large')
        if acceleration>float(m.get('collisionMaxAccelerationPercent',.75)):
            reasons.append(name+': correction adds a sharp acceleration')
        if not arm and (shift>.5 or angle>3):
            reasons.append(name+': correction moves an unrelated body part')
        changes.append({'bone':name,'affectedArm':arm,'maxCorrectionPercent':shift,'maxAddedAccelerationPercent':acceleration,'maxUnrelatedRotationDegrees':angle})
    return {'accepted':not reasons,'reasons':reasons,'characterHeight':height,'joints':changes,
            'maxCorrectionPercent':max((x['maxCorrectionPercent'] for x in changes),default=0),
            'maxAddedAccelerationPercent':max((x['maxAddedAccelerationPercent'] for x in changes),default=0)}


if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory',type=Path)
    args=parser.parse_args()
    result=report(args.directory)
    (args.directory/'collision_motion_review.json').write_text(json.dumps(result,indent=2))
    print('Accepted' if result['accepted'] else 'Rejected',result['reasons'])
