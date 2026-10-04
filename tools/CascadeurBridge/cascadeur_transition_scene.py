"""Cascadeur-side stages used by cascadeur_transition.py. Run inside Cascadeur."""
import json
from pathlib import Path
import uuid
import shutil
import numpy as np
import csc

import os
import re
CASCADEUR = Path(os.environ.get('CASCADEUR_HOME', 'Q:/_coding/tools/Cascadeur'))

def frame_body(view):
    scene = view.domain_scene()
    mv = scene.model_viewer()
    bv, dv = mv.behaviour_viewer(), mv.data_viewer()
    points = []
    for obj in mv.get_objects():
        if mv.get_object_name(obj) in {'pelvis','head','hand_l','hand_r','foot_l','foot_r'}:
            data = bv.get_behaviour_data(bv.get_behaviour_by_name(obj, 'Node3d'), 'global_matrix')
            points.append(np.asarray(dv.get_data_value(data, scene.get_current_frame()))[:3,3].astype(np.float32))
    if points:
        camera = view.active_viewport().domain_viewport().camera()
        center = np.mean(points,axis=0).astype(np.float32)
        camera.set_target(center)
        camera.zoom_to_points([center+(point-center)*1.3 for point in points])

def read_manifest(file):
    return json.loads(Path(file).read_text())

def view_for(m):
    app = csc.app.get_application()
    name = str(Path(m['directory'])/'transition.casc').replace('\\', '/')
    view = next(v for v in app.get_scene_manager().scenes()
                if v.get_path_name().replace('\\', '/') == name)
    app.get_scene_manager().set_current_scene(view)
    return app, view

def prepare_scene_slot(m):
    app = csc.app.get_application()
    filename = str(Path(m['directory'])/'transition.casc').replace('\\', '/')
    manager = app.get_scene_manager()
    for old in list(manager.scenes()):
        if old.get_path_name().replace('\\', '/') == filename:
            # Scene-idle callbacks still hold the scene being replaced.
            # Closing it from that callback can crash the app after the rig.
            old.save(str(Path(m['directory'])/('previous_'+uuid.uuid4().hex+'.casc')))
    return app, filename

def load_cached(file):
    m = read_manifest(file)
    app, filename = prepare_scene_slot(m)
    cache = Path(m['rigCache'])
    shutil.copy2(cache/'rig.casc',filename)
    shutil.copy2(cache/'interchange.json',Path(m['directory'])/'interchange.json')
    if not app.get_data_source_manager().load_scene(filename):
        raise RuntimeError('Cascadeur could not open the cached model rig')
    print('Loaded cached model rig; no rig generation needed')

def load(file):
    m = read_manifest(file)
    app, filename = prepare_scene_slot(m)
    view = app.get_scene_manager().create_application_scene()
    app.get_scene_manager().set_current_scene(view)
    app.get_tools_manager().get_tool('FbxSceneLoader').get_fbx_loader(view).import_scene(
        str(Path(m['directory'])/'rig.fbx'))
    scene = view.domain_scene()
    mv = scene.model_viewer()
    bv, dv = mv.behaviour_viewer(), mv.data_viewer()
    root = next(b for b in m['bones'] if b['exported'] and b['parent'] == m['bones'][0]['name'])
    obj = next(o for o in mv.get_objects() if mv.get_object_name(o) == root['exportName'])
    data = bv.get_behaviour_data(bv.get_behaviour_by_name(obj, 'Node3d'), 'global_matrix')
    imported = np.asarray(dv.get_data_value(data, 0), dtype=float).T
    game = np.asarray(m['canonical'][0][root['name']]).reshape(4,4)
    conversion = np.linalg.inv(imported) @ game
    (Path(m['directory'])/'interchange.json').write_text(json.dumps(conversion.tolist()))
    view.save(str(Path(m['directory'])/'transition.casc'))
    print('Imported model bind pose')

def rig(file):
    from rig_mode import on, off
    m = read_manifest(file)
    app, view = view_for(m)
    scene = view.domain_scene()
    mv = scene.model_viewer()
    bv = mv.behaviour_viewer()
    objects = {mv.get_object_name(o): o for o in mv.get_objects()
               if not bv.get_behaviour_by_name(o, 'Joint').is_null()}
    required = {'pelvis','spine_01','spine_03','neck_01','head','upperarm_l','upperarm_r',
                'lowerarm_l','lowerarm_r','hand_l','hand_r','thigh_l','thigh_r','calf_l',
                'calf_r','foot_l','foot_r','ball_l','ball_r'}
    missing = required - objects.keys()
    if missing:
        raise RuntimeError('Cannot build humanoid AI rig; missing joints: '+', '.join(sorted(missing)))
    def parents(obj):
        names = []
        while True:
            obj = bv.get_behaviour_object(bv.get_behaviour_by_name(obj, 'Basic'), 'parent')
            if obj.is_null():
                break
            names.append(mv.get_object_name(obj))
        return list(reversed(names))
    template = json.loads((CASCADEUR/'resources/autorig_templates/UE4_New.qrigcasc').read_text())
    def adapt(item):
        if isinstance(item, dict):
            if 'Joint name' in item and item['Joint name'] in objects:
                item['Joint path'] = parents(objects[item['Joint name']])
            for child in item.values():
                adapt(child)
        elif isinstance(item, list):
            for child in item:
                adapt(child)
    adapt(template)
    scene.selector().select(set(objects.values()))
    on.run_raw(scene, [.2,.6,1.])
    tool = app.get_tools_manager().get_tool('RiggingToolWindowTool').editor(view)
    tool.set_is_create_autoposing(True)
    tool.create_from_qrt_by_content(json.dumps(template))
    count = len(scene.model_viewer().behaviour_viewer().get_behaviours('TechnicalLinks'))
    if count < 19:
        raise RuntimeError('Quick Rig did not create the required body units')
    tool.set_is_create_autoposing(True)
    off.run(scene, True, False)
    app.get_tools_manager().get_tool('AutoPhysicsTool').editor(view).turn_off()
    view.save(str(Path(m['directory'])/'transition.casc'))
    print('Built humanoid rig including toes:', count, 'units')

def animation(file):
    m = read_manifest(file)
    app, view = view_for(m)
    app.get_tools_manager().get_tool('FbxSceneLoader').get_fbx_loader(view).import_animation(
        str(Path(m['directory'])/'input.fbx'))
    view.save(str(Path(m['directory'])/'transition.casc'))
    print('Imported both clips onto the generated rig')


def interpolate(file):
    m = read_manifest(file)
    app, view = view_for(m)
    scene = view.domain_scene()
    app.get_tools_manager().get_tool('InbetweeningTool').editor(view).set_update_parameter(True)
    lv = scene.layers_viewer()
    first, last = m['gapStart'], m['gapEnd']
    # Rig layers contain the body points and hand controllers. Unrigged
    # accessory tracks are handled when writing the Persona skeleton back.
    bv = scene.model_viewer().behaviour_viewer()
    objects = {bv.get_behaviour_owner(b) for b in bv.get_behaviours('AutoPosingLink')}
    layers = set(lv.layer_ids_by_obj_ids(list(objects)))
    if not layers:
        raise RuntimeError('No AutoPosing tracks available for AI interpolation')
    def edit(model, update, updater, session):
        editor = model.layers_editor()
        for layer in layers:
            for frame in range(first+1, last):
                editor.unset_section(frame, layer)
            editor.set_fixed_interpolation_or_key_if_need(layer, first, True)
            editor.set_fixed_interpolation_or_key_if_need(layer, last, True)
            def ai(section):
                section.interval.interpolation = csc.layers.layer.Interpolation.AI
                section.interval.description_for_ai_interpolation = m['motion']
            editor.change_section(first, layer, ai)
    if not scene.modify_update_with_session('Generate AI between the two clips', edit):
        raise RuntimeError('Cascadeur rejected the AI interval')
    app.get_tools_manager().get_tool('InbetweeningTool').editor(view).set_update_parameter(True)
    scene.set_current_frame((first+last)//2)
    scene.selector().select(set())
    app.get_action_manager().call_action('VisualizerStateController.AllModes.View mode')
    frame_body(view)
    view.save(str(Path(m['directory'])/'transition.casc'))
    print('AI interval:', first, last, m['motion'])

def bake(file):
    m = read_manifest(file)
    app, view = view_for(m)
    scene = view.domain_scene()
    mv = scene.model_viewer()
    bv, dv = mv.behaviour_viewer(), mv.data_viewer()
    objects = {mv.get_object_name(o): o for o in mv.get_objects()
               if not bv.get_behaviour_by_name(o, 'Joint').is_null()}
    joints = {}
    for bone in m['bones']:
        if not bone['exported']:
            continue
        if bone['exportName'] not in objects:
            if bone['humanoid']:
                raise RuntimeError('Baked skeleton lost '+bone['exportName'])
            continue
        joints[bone['name']] = bv.get_behaviour_data(
            bv.get_behaviour_by_name(objects[bone['exportName']], 'Node3d'), 'global_matrix')
    frames = []
    conversion = np.asarray(json.loads((Path(m['directory'])/'interchange.json').read_text()))
    for frame in range(m['gapStart'], m['gapEnd']+1):
        pose = {name: (np.asarray(dv.get_data_value(data, frame), dtype=float).T @ conversion).reshape(-1).tolist()
                for name, data in joints.items()}
        frames.append(pose)
    if len(frames) > 2:
        body = [name for name in ('Hips', 'LeftHand', 'RightHand', 'LeftFoot', 'RightFoot') if name in frames[0]]
        def difference(pose):
            return max((abs(a-b) for name in body for a,b in zip(frames[0][name], pose[name])), default=0.0)
        if difference(frames[-1]) > 1e-4 and max(map(difference, frames[1:-1])) < 1e-4:
            raise RuntimeError('Cascadeur AI held the first pose through the whole transition; refusing to export a frozen middle')
    output = Path(m['directory'])/'baked.json'
    output.write_text(json.dumps({'frames': frames}, separators=(',',':')))
    print('Baked', len(frames), 'frames on', len(joints), 'game joints')

def preview(file):
    m = read_manifest(file)
    app = csc.app.get_application()
    manager = app.get_scene_manager()
    filename = str(Path(m['directory'])/'game_roundtrip.casc').replace('\\', '/')
    for old in list(manager.scenes()):
        if old.get_path_name().replace('\\', '/') == filename:
            old.save(str(Path(m['directory'])/('previous_preview_'+uuid.uuid4().hex+'.casc')))
    view = manager.create_application_scene()
    manager.set_current_scene(view)
    app.get_tools_manager().get_tool('FbxSceneLoader').get_fbx_loader(view).import_scene(
        str(Path(m['directory'])/'joined_game_model.fbx'))
    view.domain_scene().set_current_frame((m['gapStart']+m['gapEnd'])//2-1)
    frame_body(view)
    view.save(filename)
    print('Opened GAP with game skin bindings')

def generated_scene(view):
    """Recognize helper-created tabs without closing user documents."""
    file = Path(view.get_path_name())
    if file.name not in {'transition.casc', 'game_roundtrip.casc'} and not file.name.startswith('previous_'):
        return False
    manifest = file.parent/'manifest.json'
    if not manifest.is_file():
        return False
    try:
        return Path(read_manifest(manifest)['directory']).resolve() == file.parent.resolve()
    except (OSError, ValueError, KeyError):
        return False


def release(file):
    # Change tabs first; the following idle call can then close the job scene.
    manager = csc.app.get_application().get_scene_manager()
    user_scene = next((view for view in manager.scenes() if not generated_scene(view)), None)
    if user_scene is None:
        user_scene = manager.create_application_scene()
    manager.set_current_scene(user_scene)


def close_generated(file, idle_scene):
    manager = csc.app.get_application().get_scene_manager()
    keep = manager.current_scene()
    count = 0
    for old in list(manager.scenes()):
        if old == keep or old.domain_scene() == idle_scene or not generated_scene(old):
            continue
        manager.remove_application_scene(old)
        count += 1
    print('Closed', count, 'generated scene tabs')


def reconnect_ai(file):
    # The first AI edit leaves imported GLOBAL tracks disconnected from the
    # inbetweening core. Reload with the AI intervals already present, then
    # regenerate against the loaded controller data before baking.
    m = read_manifest(file)
    app, view = view_for(m)
    view.save(str(Path(m['directory'])/'previous_ai.casc'))
    if not app.get_data_source_manager().load_scene(str(Path(m['directory'])/'transition.casc')):
        raise RuntimeError('Cascadeur could not reopen the AI interval')
    interpolate(file)


def clean_collisions(file):
    m = read_manifest(file)
    if not m.get('cleanCollisions', True):
        return
    app, view = view_for(m)
    # A new automatic AI update would replace edits made by penetration cleaning.
    app.get_tools_manager().get_tool('InbetweeningTool').editor(view).set_update_parameter(False)
    scene = view.domain_scene()
    mv = scene.model_viewer()
    bv, dv = mv.behaviour_viewer(), mv.data_viewer()
    behaviours = bv.get_behaviours('CollisionPenetrationCleaning')
    affected = set(m.get('collisionAffectedSides', []))
    def affected_arm(behaviour):
        name = mv.get_object_name(bv.get_behaviour_owner(behaviour)).lower()
        suffix = re.search(r'(?:^|[_ ])([lr])(?:[_ ]|$)', name)
        side = suffix.group(1) if suffix else 'l' if 'left' in name else 'r' if 'right' in name else None
        return side in affected and any(part in name for part in ('arm','elbow','wrist','hand','finger','thumb','index','middle','ring','pinky'))
    movable = {b for b in behaviours if affected_arm(b)}

    if not behaviours:
        raise RuntimeError('The rig has no collision-cleaning controllers; rebuild the rig with Cascadeur 2026.1 or newer')
    stiffness = float(m.get('collisionMuscleStiffness', 15))
    if not 0 <= stiffness < 100:
        raise ValueError('Collision stiffness must be 0..99; 100 prevents correction')
    # Quick Rig scales capsule lengths with the skeleton, but its template
    # limb radii can stay in centimeters after a much larger FBX import.
    # A needle-sized forearm collider cannot guard the visible forearm.
    conversion = np.asarray(json.loads((Path(m['directory'])/'interchange.json').read_text()))
    inverse_conversion = np.linalg.inv(conversion)
    bind = [(np.asarray(b['bindWorld']).reshape(4,4) @ inverse_conversion)[3,:3]
            for b in m['bones'] if b['humanoid']]
    height = max(1., max(p[1] for p in bind)-min(p[1] for p in bind))
    padding = float(m.get('collisionPaddingPercent', 0))
    if not 0 <= padding <= 100:
        raise ValueError('Collision padding must be 0..100 percent')
    proportions = {'lowerarm': .018, 'upperarm': .023, 'calf': .023, 'thigh': .033}
    if padding > 0:
        proportions.update(head=.0395, neck_01=.02325)
    radii = []
    for behaviour in bv.get_behaviours('CapsuleCollision'):
        name = mv.get_object_name(bv.get_behaviour_owner(behaviour))
        part = next((part for part in proportions if name.startswith(part+'_')), None)
        if part is None:
            continue
        radius_id = bv.get_behaviour_data(behaviour, 'radius')
        old = float(dv.union_get_data_value(radius_id))
        radius = max(old, height*proportions[part]*(1+padding/100))
        if radius > old+1e-4:
            radii.append((radius_id, radius, name, old))
    def edit(model, update, updater, session):
        for radius_id, radius, name, old in radii:
            model.data_editor().set_data_value(radius_id, radius)
        for behaviour in behaviours:
            model.data_editor().set_data_value(bv.get_behaviour_data(behaviour, 'muscle_stiffness'),
                set(range(m['gapStart'], m['gapEnd']+1)), stiffness if behaviour in movable else 100)
    scene.modify_update_with_session('Allow collision penetration correction', edit)
    points = {bv.get_behaviour_owner(b) for b in movable}
    scene.selector().select(points)
    layers = list(scene.layers_viewer().layer_ids_by_obj_ids(list(points)))
    scene.get_layers_selector().set_full_selection_by_parts(layers, m['gapStart']+1, m['gapEnd']-1)
    if points:
        app.get_action_manager().call_action('View.FixCollisions')
    view.save(str(Path(m['directory'])/'transition.casc'))
    (Path(m['directory'])/'collision_cleaning.json').write_text(json.dumps({
        'enabled': bool(points), 'controllers': len(points), 'frozenControllers': len(behaviours)-len(movable), 'affectedSides': sorted(affected), 'stiffness': stiffness,
        'first': m['gapStart']+1, 'last': m['gapEnd']-1,
        'action': 'View.FixCollisions', 'colliderHeight': height, 'paddingPercent': padding,
        'resizedLimbColliders': [{'name': name, 'before': old, 'after': radius}
                                for _, radius, name, old in radii], 'muscleFrames': list(range(m['gapStart'], m['gapEnd']+1))}))
    print('Collision cleaning finished on', len(points), 'controllers')


def load_saved(file):
    """Open a finished AI scene for another penetration-cleaning pass."""
    m = read_manifest(file)
    app = csc.app.get_application()
    filename = str(Path(m['directory'])/'transition.casc').replace('\\', '/')
    view = next((v for v in app.get_scene_manager().scenes()
                 if v.get_path_name().replace('\\', '/') == filename), None)
    if view is not None:
        app.get_scene_manager().set_current_scene(view)
    elif not app.get_data_source_manager().load_scene(filename):
        raise RuntimeError('Cascadeur could not open the saved transition for cleaning')


def collision_guard(file):
    import runpy
    m = read_manifest(file)
    directory = Path(m['directory'])
    report = runpy.run_path(str(Path(__file__).with_name('transition_collision_report.py')))['report'](directory)
    (directory/'head_neck_review.json').write_text(json.dumps(report, indent=2))
    print('Head/neck clearance review:', report.get('flaggedFrames', 0), 'flagged frames')


def seal_middle(file):
    """Keep the generated and collision-corrected poses as per-frame keys."""
    m = read_manifest(file)
    app, v = view_for(m)
    d = Path(m['directory'])
    scene = v.domain_scene()
    mv = scene.model_viewer()
    bv, dv = mv.behaviour_viewer(), mv.data_viewer()
    objects={bv.get_behaviour_owner(b) for b in bv.get_behaviours('CollisionPenetrationCleaning')};layers=set(scene.layers_viewer().layer_ids_by_obj_ids(list(objects)));snapshots=[]
    for obj in objects:
     data=bv.get_behaviour_data(bv.get_behaviour_by_name(obj,'Transform'),'global_position')
     snapshots.append((data,{f:np.asarray(dv.get_data_value(data,f),dtype=np.float32).copy() for f in range(m['gapStart']+1,m['gapEnd'])}))
    def edit(model,update,updater,session):
     le=model.layers_editor();de=model.data_editor()
     for layer in layers:
      for frame in range(m['gapStart']+1,m['gapEnd']):le.set_fixed_interpolation_or_key_if_need(layer,frame,True)
      for frame in range(m['gapStart'],m['gapEnd']):
       def fixed(section):section.interval.interpolation=csc.layers.layer.Interpolation.FIXED
       le.change_section(frame,layer,fixed)
     for data,frames in snapshots:
      for frame,value in frames.items():de.set_data_value(data,frame,value)
    scene.modify_update_with_session('Keep the collision-corrected middle on every frame',edit)
    v.save(str(d/'transition.casc'))
    (d/'sealed_middle.json').write_text(json.dumps({'points':len(objects), 'layers':len(layers)}))


def snapshot_uncleaned(file):
    """Save controller keys and a scene before any collision correction."""
    m=read_manifest(file)
    app,view=view_for(m)
    app.get_tools_manager().get_tool('InbetweeningTool').editor(view).set_update_parameter(False)
    scene=view.domain_scene();mv=scene.model_viewer();bv=mv.behaviour_viewer();dv=mv.data_viewer()
    points={bv.get_behaviour_owner(b) for b in bv.get_behaviours('CollisionPenetrationCleaning')}
    poses={}
    for point in points:
        data=bv.get_behaviour_data(bv.get_behaviour_by_name(point,'Transform'),'global_position')
        poses[mv.get_object_name(point)]={str(f):np.asarray(dv.get_data_value(data,f),dtype=np.float32).tolist()
            for f in range(m['gapStart'],m['gapEnd']+1)}
    directory=Path(m['directory'])
    (directory/'uncleaned_controllers.json').write_text(json.dumps(poses))
    view.save(str(directory/'transition.casc'))
    shutil.copy2(directory/'transition.casc',directory/'uncleaned.casc')


def restore_uncleaned(file):
    """Put the saved pre-collision controller poses back on every frame."""
    m=read_manifest(file);app,view=view_for(m);scene=view.domain_scene()
    app.get_tools_manager().get_tool('InbetweeningTool').editor(view).set_update_parameter(False)
    mv=scene.model_viewer();bv=mv.behaviour_viewer()
    stored=json.loads((Path(m['directory'])/'uncleaned_controllers.json').read_text())
    points={mv.get_object_name(o):o for o in mv.get_objects() if mv.get_object_name(o) in stored}
    if set(points)!=set(stored):raise RuntimeError('Saved pre-collision controllers are missing')
    layers=set(scene.layers_viewer().layer_ids_by_obj_ids(list(points.values())))
    def edit(model,update,updater,session):
        le=model.layers_editor();de=model.data_editor()
        for layer in layers:
            for f in range(m['gapStart']+1,m['gapEnd']):le.set_fixed_interpolation_or_key_if_need(layer,f,True)
            for f in range(m['gapStart'],m['gapEnd']):
                def fixed(section):section.interval.interpolation=csc.layers.layer.Interpolation.FIXED
                le.change_section(f,layer,fixed)
        for name,obj in points.items():
            data=bv.get_behaviour_data(bv.get_behaviour_by_name(obj,'Transform'),'global_position')
            for frame,value in stored[name].items():de.set_data_value(data,int(frame),np.asarray(value,dtype=np.float32))
    scene.modify_update_with_session('Reject disruptive collision correction',edit)
    view.save(str(Path(m['directory'])/'transition.casc'))
