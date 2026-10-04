"""Flag likely forearm/head or forearm/neck penetration for visual review.

Capsules approximate the body; this report is not a mesh collision guarantee.
It reads the Cascadeur baked middle and never edits animation.
"""
import argparse
import json
from pathlib import Path
import numpy as np


def segment_distance(a,b,c,d):
    u=b-a;v=d-c;w=a-c
    aa=float(u@u);bb=float(u@v);cc=float(v@v);dd=float(u@w);ee=float(v@w)
    if aa<1e-12 and cc<1e-12:return float(np.linalg.norm(a-c))
    if aa<1e-12:s=0.;t=float(np.clip(ee/max(cc,1e-12),0,1))
    elif cc<1e-12:t=0.;s=float(np.clip(-dd/aa,0,1))
    else:
        den=aa*cc-bb*bb;s=float(np.clip((bb*ee-cc*dd)/den,0,1)) if den>1e-12 else 0.
        t=(bb*s+ee)/cc
        if t<0:t=0.;s=float(np.clip(-dd/aa,0,1))
        elif t>1:t=1.;s=float(np.clip((bb-dd)/aa,0,1))
    return float(np.linalg.norm(w+s*u-t*v))


def report(folder, bake_name='baked.json'):
    m=json.loads((folder/'manifest.json').read_text());frames=json.loads((folder/bake_name).read_text())['frames']
    bones={b['exportName']:b['name'] for b in m['bones'] if b['humanoid']}
    bind=[np.asarray(b['bindWorld'])[12:15] for b in m['bones'] if b['humanoid']]
    height=max(1.,max(p[1] for p in bind)-min(p[1] for p in bind))
    required=['neck_01','head','lowerarm_l','hand_l','lowerarm_r','hand_r']
    if any(name not in bones for name in required):return {'folder':folder.name,'available':False}
    head_bind=np.asarray(next(b['bindWorld'] for b in m['bones'] if b['name']==bones['head'])).reshape(4,4)
    head_up_local=np.asarray([0.,1.,0.])@np.linalg.inv(head_bind[:3,:3])
    events=[]
    for i,pose in enumerate(frames[1:-1],1):
        p={name:np.asarray(pose[bones[name]])[12:15] for name in required}
        neck_axis=p['head']-p['neck_01'];neck_length=float(np.linalg.norm(neck_axis))
        neck_axis=neck_axis/max(1e-8,neck_length)
        neck_radius=.025*height
        neck_center=(p['neck_01']+p['head'])*.5
        neck_half=max(0.,neck_length*.5-neck_radius)
        # The head joint is at the skull base, not at the skull center.
        # Capsule endpoints are sphere centers: subtract the end radii from
        # the full neck length rather than extending past both neck joints.
        head_up=head_up_local@np.asarray(pose[bones['head']]).reshape(4,4)[:3,:3]
        head_up=head_up/max(1e-8,float(np.linalg.norm(head_up)))
        head_center=p['head']+head_up*(.04*height)
        for side in ['l','r']:
            a,b=p['lowerarm_'+side],p['hand_'+side]
            for part,c,d,radius in [('neck',neck_center-neck_axis*neck_half,neck_center+neck_axis*neck_half,.025),('head',head_center,head_center,.045)]:
                depth=(.020+radius)-segment_distance(a,b,c,d)/height
                if depth>.01:events.append({'frame':m['gapStart']+i,'joinedFrame':m['gapStart']+i-1,'betweenFrame':i,'part':part,'side':side,'depthRelativeToHeight':float(round(depth,5))})
    return {'folder':folder.name,'available':True,'approximateCapsules':True,'flaggedFrames':len(set(e['frame'] for e in events)),'maxDepthRelativeToHeight':max((e['depthRelativeToHeight'] for e in events),default=0),'events':events}


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('directory',type=Path);args=parser.parse_args()
    reports=[report(p.parent) for p in sorted(args.directory.glob('r*/baked.json'))]
    (args.directory/'collision_review.json').write_text(json.dumps(reports,indent=2))
    print('Reviewed',len(reports),'middles; flagged',sum(x.get('flaggedFrames',0)>0 for x in reports),'for visual review')


if __name__=='__main__':main()
