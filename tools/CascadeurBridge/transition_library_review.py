"""Render GAP joins and build a 30 fps animated review page for a transition library."""
import argparse
import html
import json
import os
import subprocess
from pathlib import Path
from PIL import Image, ImageDraw

DEFAULT_BRIDGE = Path('Q:/_coding/GFD-Studio/GFDStudio/bin/x64/Release/net8.0-windows/win-x64/RetargetProbe.exe')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--bridge', type=Path, default=DEFAULT_BRIDGE)
    parser.add_argument('--from-images', action='store_true', help='Use the frame pictures already rendered')
    parser.add_argument('--yaw', type=int, default=0, help='Rotate the review camera, for example 90 for a side view')
    args = parser.parse_args()
    base = args.directory.resolve()
    if not args.from_images:
        env=dict(os.environ, GFD_REVIEW_YAW=str(args.yaw))
        subprocess.run([str(args.bridge), '--cascadeur-job', 'render-library', str(base/'library_job.json')], check=True, env=env)
    plan = json.loads((base/'poses.json').read_text())
    report_file = base/'reuse_report.json'
    reused = json.loads(report_file.read_text()) if report_file.exists() else []
    out = base/('visual_review' if args.yaw == 0 else f'visual_review_yaw_{args.yaw}')
    sections = []
    for kind in ['generated', 'reuse']:
        groups = {}
        for file in sorted(out.glob(kind+'_[0-9][0-9]_f*.png')):
            groups.setdefault(int(file.stem.split('_')[1]), []).append(file)
        cards, thumbs = [], []
        for i, files in groups.items():
            frames = []
            for file in files:
                with Image.open(file) as picture:
                    frames.append(picture.convert('RGB').crop((600, 50, 1200, 700)))
            gif = f'{kind}_{i:02d}.gif'
            # GIF time units are 10 ms: 30/30/40 ms gives 30 fps without drift.
            timing = [30 if j % 3 != 2 else 40 for j in range(len(frames))]
            timing[-1] = 500
            frames[0].save(out/gif, save_all=True, append_images=frames[1:], duration=timing, loop=0)
            sample = plan['selectedSamples'][i if kind == 'generated' else plan['generatedCount']+i]
            detail = f"Rank {sample['rank']} · dance clip {sample['sourceClip']}, frame {sample['frame']}"
            if kind == 'reuse':
                donor = reused[i]
                detail += f" · donor {donor['donorClip']+1} · entry jump {100*donor['maxEntryJumpRelativeToHeight']:.1f}% of height"
            else:
                detail += f" · {sample['transitionFrames']} interior frames · B frame {sample['bStartFrame']}"
            cards.append(f'<article><h2>{i+1}</h2><p>{html.escape(detail)}</p><img loading="lazy" src="{gif}" alt="Animated join {i+1}"></article>')
            thumb = frames[len(frames)//2].copy()
            thumb.thumbnail((220, 238))
            tile = Image.new('RGB', (240, 275), '#24282c')
            tile.paste(thumb, ((240-thumb.width)//2, 27))
            ImageDraw.Draw(tile).text((8, 8), f'{kind} #{i+1}', fill='white')
            thumbs.append(tile)
        if thumbs:
            board = Image.new('RGB', (1440, 275*((len(thumbs)+5)//6)), '#24282c')
            for i, tile in enumerate(thumbs):
                board.paste(tile, ((i%6)*240, (i//6)*275))
            board.save(out/f'{kind}_contact_sheet.png')
        sections.append(f'<section id="{kind}" {"hidden" if kind == "reuse" else ""}>'+''.join(cards)+'</section>')
        print(kind, len(groups), 'animated joins', flush=True)
    page = '''<!doctype html><html><meta charset="utf-8"><title>Transition library review</title>
<style>body{background:#24282c;color:#eee;font:16px system-ui;margin:24px}button{padding:12px;margin-right:12px;cursor:pointer}section{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:20px}section[hidden]{display:none}article{background:#30363c;padding:14px;border-radius:8px}img{width:100%}h2{margin:0}p{min-height:3em}header{position:sticky;top:0;background:#24282c;padding:12px 0;z-index:1}</style>
<header><h1>Transition library review</h1><p>30 fps. Each clip shows the dance join, middle and beginning of B. Borrowed joins contain no added smoothing. The camera follows the figure; animation root movement is retained in the GAPs.</p>
<button onclick="show('generated')">New middles</button><button onclick="show('reuse')">Borrowed middles</button></header>
'''+''.join(sections)+'''<script>function show(id){for(const s of document.querySelectorAll('section'))s.hidden=s.id!==id}</script></html>'''
    (out/'review.html').write_text(page, encoding='utf-8')
    print('Open', out/'review.html')


if __name__ == '__main__':
    main()
