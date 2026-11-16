"""
Animates the procedural lion and renders one state as transparent PNG frames (with a soft contact shadow).

  blender -b -P tools/animate_lion.py -- '{"state": "idle", "outdir": "design/anim", "design": "design/chosen.json"}'

States: idle, thinking, talking, happy, dragged, landing, turn, hop, sleep, hover
"""
import bpy, json, math, os, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("procedural_lion", os.path.join(HERE, "procedural_lion.py"))
lion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lion)  # guarded: doesn't render on import

FPS = 15
STATES = {  # name: (frames, loops)
    "idle": (60, True), "thinking": (60, True), "talking": (30, True), "happy": (24, False),
    "dragged": (30, True), "landing": (12, False), "turn": (8, False), "hop": (12, True),
    "sleep": (60, True), "hover": (20, False),
    "stand_up": (10, False), "walk": (16, True),   # standing pose (on all fours)
    # Quirks / tricks
    "shake_head": (18, False), "jump": (22, False), "stretch_yawn": (40, False), "tail_chase": (36, False),
    "wave": (24, False), "look_roar": (44, False), "look_up": (30, True), "swat": (14, False),
}
STANDING_STATES = {"stand_up", "walk"}
FACING_RIGHT = 90   # degrees around Z: the lion faces screen-right when leaving

HEAD_PREFIX = ("head", "mane_core", "curl_", "ear", "cheek", "chin", "nose", "smile", "mouth", "tongue",
               "blush", "eye", "glint", "iris", "pupil", "brow")

def empty(name, loc, parent=None):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.1
    bpy.context.collection.objects.link(e)
    e.location = loc
    if parent:
        e.parent = parent
        e.matrix_parent_inverse = parent.matrix_world.inverted()
    bpy.context.view_layer.update()
    return e

def attach(obj, parent):
    mw = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_parent_inverse = parent.matrix_world.inverted()
    obj.matrix_world = mw

def rig(p):
    """Group the primitive parts under pivots so they can be animated."""
    objs = [o for o in bpy.context.scene.objects if o.type in ("MESH", "CURVE")]
    hs = p["head_scale"]
    hc = lion.head_center(p)
    standing = p.get("pose") == "standing"
    R = {}
    R["root"] = empty("root", (0, 0, 0))
    R["body"] = empty("body_grp", (0, 0, 0.05), R["root"])
    if standing:
        R["head"] = empty("head_grp", (0, -0.45, 0.85), R["body"])
        R["tail"] = empty("tail_grp", (0, 0.85, 0.72), R["body"])
    else:
        R["head"] = empty("head_grp", (0, -0.05, 0.95), R["body"])
        R["tail"] = empty("tail_grp", (0.3, 0.45, 0.25), R["body"])
    for s in (-1, 1):
        if standing:
            R[f"leg{s}"] = empty(f"leg_grp{s}", (0.22 * s, -0.32, 0.6), R["body"])
            R[f"bleg{s}"] = empty(f"bleg_grp{s}", (0.22 * s, 0.58, 0.6), R["body"])
        else:
            R[f"leg{s}"] = empty(f"leg_grp{s}", (0.2 * s, -0.25, 0.78), R["body"])
        R[f"ear{s}"] = empty(f"ear_grp{s}", tuple(hc + Vector((0.34 * s * hs, -0.12, 0.42 * hs))), R["head"])
        R[f"eye{s}"] = empty(f"eye_grp{s}", tuple(hc + Vector((0.2 * s * hs, -0.42 * hs, 0.1 * hs))), R["head"])
    bpy.context.view_layer.update()
    def side_of(n, prefixes):
        for pre in prefixes:  # longest prefixes first, e.g. "glint2" before "glint"
            if n.startswith(pre):
                return -1 if n[len(pre):].startswith("-1") else 1
        return 1

    for o in objs:
        n = o.name
        if n.startswith(("eye", "glint", "iris", "pupil")):
            attach(o, R[f"eye{side_of(n, ('glint2', 'glint3', 'glint', 'eye', 'iris', 'pupil'))}"])
        elif n.startswith("ear"):
            attach(o, R[f"ear{side_of(n, ('earin', 'ear'))}"])
        elif n.startswith(("leg", "paw", "toe")):
            attach(o, R[f"leg{side_of(n, ('leg', 'paw', 'toe'))}"])
        elif n.startswith(("bleg", "bpaw", "btoe")):
            attach(o, R[f"bleg{side_of(n, ('bleg', 'bpaw', 'btoe'))}"])
        elif n.startswith(HEAD_PREFIX):
            attach(o, R["head"])
        elif n.startswith(("tail", "tuft")):
            attach(o, R["tail"])
        else:
            attach(o, R["body"])
    return R

def key(obj, frame, loc=None, rot=None, scale=None):
    if loc is not None:
        obj.location = loc
        obj.keyframe_insert("location", frame=frame)
    if rot is not None:
        obj.rotation_euler = [math.radians(a) for a in rot]
        obj.keyframe_insert("rotation_euler", frame=frame)
    if scale is not None:
        obj.scale = scale
        obj.keyframe_insert("scale", frame=frame)

def blink_amount(f, at, dur=4):
    """1 = open, ~0.1 = closed, smooth close/open around frame `at`."""
    d = abs(f - at)
    return 1.0 if d >= dur / 2 + 1 else max(0.1, d / (dur / 2 + 1))

def animate(state, R):
    frames, _ = STATES[state]
    base = {k: (v.location.copy(), v.rotation_euler.copy()) for k, v in R.items()}
    tau = math.tau
    lifts = []
    for f in range(frames):
        t = f / frames                     # 0..1 over the loop
        body_loc, body_rot, body_s = Vector(base["body"][0]), [0, 0, 0], [1, 1, 1]
        root_loc, root_rot = Vector((0, 0, 0)), [0, 0, 0]
        head_rot, tail_rot = [0, 0, 0], [0, 0, 0]
        leg_rot = {-1: [0, 0, 0], 1: [0, 0, 0]}
        bleg_rot = {-1: [0, 0, 0], 1: [0, 0, 0]}
        ear_rot = {-1: [0, 0, 0], 1: [0, 0, 0]}
        eye = 1.0
        mouth_open = False
        breathe = math.sin(t * tau * 2)
        def ramp(a, b):   # 0→1 between frames a..b (smooth)
            return 0.5 - 0.5 * math.cos(math.pi * min(1, max(0, (f - a) / max(1, b - a))))

        if state == "shake_head":        # quick "nope" shake, decaying
            k = 1 - f / frames
            head_rot = [0, 0, 24 * math.sin(f / frames * tau * 2.5) * k]
            for s in (-1, 1):
                ear_rot[s] = [0, 10 * s * math.sin(f / frames * tau * 2.5 + 0.5) * k, 0]
            tail_rot = [0, 0, 12 * math.sin(t * tau * 2)]
        elif state == "jump":            # crouch, big jump with a full spin, land with a squash
            if f < 4:
                sq = math.sin(f / 4 * math.pi / 2)
                body_s = [1 + 0.1 * sq, 1 + 0.1 * sq, 1 - 0.15 * sq]
            elif f < 17:
                u = (f - 4) / 13
                root_loc.z = 0.55 * 4 * u * (1 - u)
                root_rot = [0, 0, 360 * (0.5 - 0.5 * math.cos(math.pi * u))]
                body_s = [0.94, 0.94, 1.1] if u < 0.4 else [1, 1, 1]
                for s in (-1, 1):
                    leg_rot[s] = [-30 * math.sin(u * math.pi), 0, 0]
                tail_rot = [-35, 0, 0]
                head_rot = [-10, 0, 0]
            else:
                u = (f - 17) / 5
                sq = math.sin(min(1, u) * math.pi)
                body_s = [1 + 0.09 * sq, 1 + 0.09 * sq, 1 - 0.13 * sq]
        elif state == "stretch_yawn":    # leans forward with paws stretched out, big yawn, settles
            k = ramp(0, 12) * (1 - ramp(30, 40))
            body_rot = [-14 * k, 0, 0]
            body_loc.z -= 0.06 * k
            for s in (-1, 1):
                leg_rot[s] = [-38 * k, 0, 0]
            y = ramp(12, 18) * (1 - ramp(28, 33))
            head_rot = [-22 * y, 0, 0]
            mouth_open = 13 <= f <= 30
            eye = 0.1 if 13 <= f <= 30 else 1.0
            tail_rot = [-15 * k, 0, 10 * math.sin(t * tau)]
        elif state == "tail_chase":      # spins twice after his tail, then a dizzy wobble
            spin = min(1, f / 30)
            root_rot = [0, 0, 720 * (0.5 - 0.5 * math.cos(math.pi * spin))]
            body_rot = [0, 10 * math.sin(math.pi * spin), 0]
            tail_rot = [-30, 0, 25]
            head_rot = [0, 0, -20 * math.sin(math.pi * spin)]
            if f >= 30:
                w = (f - 30) / 6
                head_rot = [0, 12 * math.sin(w * tau * 1.5) * (1 - w), 0]
                eye = 0.55
        elif state == "wave":            # lifts a paw and waves
            k = ramp(0, 5) * (1 - ramp(19, 24))
            # Paw goes out to the side and up (not in front of the face), then waves.
            leg_rot[1] = [-35 * k, (-125 + 18 * math.sin(f / 24 * tau * 3)) * k, 0]
            head_rot = [0, 8 * k, 6 * k]
            body_rot = [0, -4 * k, 0]
            tail_rot = [0, 0, 20 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 12)
        elif state == "look_roar":       # looks left, right, then a tiny cute roar
            look = 0
            if f < 8: look = -35 * ramp(0, 8)
            elif f < 16: look = -35 + 70 * ramp(8, 16)
            elif f < 22: look = 35 * (1 - ramp(16, 22))
            r = ramp(23, 27) * (1 - ramp(38, 43))
            head_rot = [-14 * r, 0, look]
            body_s = [1, 1, 1 + 0.04 * r]
            for s in (-1, 1):
                ear_rot[s] = [0, (12 if f < 22 else -18 * r) * s * (1 if f < 22 else 1), 0]
            mouth_open = 26 <= f <= 38
            eye = 0.6 if 26 <= f <= 38 else blink_amount(f, 19)
            tail_rot = [-25 * r, 0, 10 * math.sin(t * tau * 3)]
        elif state == "look_up":         # watching the butterfly above him
            head_rot = [-24, 0, 12 * math.sin(t * tau)]
            for s in (-1, 1):
                ear_rot[s] = [0, -10 * s, 0]
            tail_rot = [0, 0, 22 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 20)
        elif state == "swat":            # quick paw swipe upward
            k = math.sin(min(1, f / 5) * math.pi / 2) * (1 - ramp(7, 14))
            leg_rot[1] = [-20 * k, -150 * k, 0]          # high up and out, toward the butterfly
            head_rot = [-22, 0, 0]
            body_rot = [-4 * k, 6 * k, 0]
            tail_rot = [-20 * k, 0, 15]
        elif state == "walk":              # 4-legged walk cycle, facing right; diagonal leg pairs move together
            root_rot = [0, 0, FACING_RIGHT]
            ph = t * tau
            swing = 26
            leg_rot[-1] = [swing * math.sin(ph), 0, 0]
            bleg_rot[1] = [swing * math.sin(ph), 0, 0]
            leg_rot[1] = [swing * math.sin(ph + math.pi), 0, 0]
            bleg_rot[-1] = [swing * math.sin(ph + math.pi), 0, 0]
            body_loc.z += 0.025 * abs(math.cos(ph))        # bob twice per cycle
            body_rot = [1.5 * math.sin(ph * 2), 2 * math.sin(ph), 0]
            head_rot = [3 * math.sin(ph * 2 + 0.6), 0, 2 * math.sin(ph)]
            tail_rot = [8 * math.sin(ph * 2), 0, 14 * math.sin(ph)]
            eye = blink_amount(f, 9) if f in range(6, 13) else 1.0
        elif state == "stand_up":        # rises from a crouch onto four legs, already facing right
            root_rot = [0, 0, FACING_RIGHT]
            u = math.sin(f / (frames - 1) * math.pi / 2)  # ease out
            crouch = 1 - u
            root_loc.z = -0.16 * crouch
            body_rot = [-12 * crouch, 0, 0]                  # rear lower than chest, like getting up from sitting
            for s in (-1, 1):
                leg_rot[s] = [-10 * crouch, 0, 0]
                bleg_rot[s] = [40 * crouch, 0, 0]          # back legs tucked forward under the body
            tail_rot = [-20 * crouch, 0, 10 * math.sin(f / 3)]
        elif state == "idle":
            body_s = [1, 1, 1 + 0.015 * breathe]
            head_rot = [2 * math.sin(t * tau), 0, 6 * math.sin(t * tau)]
            tail_rot = [0, 0, 18 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 42)
            if 18 <= f <= 24:
                ear_rot[1] = [0, -18 * math.sin((f - 18) / 6 * math.pi), 0]
        elif state == "hover":           # perks up: ears up, small lean forward, happy blink
            k = math.sin(min(1, f / 8) * math.pi / 2)
            head_rot = [-6 * k, 0, 0]
            body_rot = [-3 * k, 0, 0]
            for s in (-1, 1):
                ear_rot[s] = [0, -12 * s * k, 0]
            tail_rot = [0, 0, 25 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 14)
        elif state == "thinking":
            head_rot = [-10, 12 + 3 * math.sin(t * tau), 4 * math.sin(t * tau)]
            tail_rot = [0, 0, 8 * math.sin(t * tau * 4)]
            body_s = [1, 1, 1 + 0.01 * breathe]
            leg_rot[-1] = [-25 - 5 * math.sin(t * tau * 2), 0, 0]   # lifts a paw to its chin
            eye = blink_amount(f, 30)
        elif state == "talking":
            head_rot = [6 * math.sin(t * tau * 2), 0, 4 * math.sin(t * tau)]
            body_loc.z += 0.015 * abs(math.sin(t * tau * 2))
            tail_rot = [0, 0, 14 * math.sin(t * tau * 2)]
        elif state == "happy":           # little hop with squash & stretch
            if f < 4:
                sq = math.sin(f / 4 * math.pi / 2)
                body_s = [1 + 0.08 * sq, 1 + 0.08 * sq, 1 - 0.12 * sq]
            elif f < 16:
                u = (f - 4) / 12
                root_loc.z = 0.35 * 4 * u * (1 - u)
                body_s = [0.95, 0.95, 1.08] if u < 0.5 else [1, 1, 1]
                for s in (-1, 1):
                    leg_rot[s] = [20 * math.sin(u * math.pi), 0, 0]
                tail_rot = [-25, 0, 0]
            else:
                u = (f - 16) / 8
                sq = math.sin(u * math.pi)
                body_s = [1 + 0.07 * sq, 1 + 0.07 * sq, 1 - 0.1 * sq]
            head_rot = [-6, 0, 0]
        elif state == "dragged":         # lifted by the scruff, legs dangling
            root_loc.z = 0.25
            body_rot = [0, 6 * math.sin(t * tau), 0]
            for s in (-1, 1):
                leg_rot[s] = [25 + 15 * math.sin(t * tau * 2 + (0 if s > 0 else math.pi)), 0, 0]
            tail_rot = [20, 0, 20 * math.sin(t * tau * 2)]
            head_rot = [-8, 0, 0]
            eye = 1.0
        elif state == "landing":         # bump down after being dropped
            u = f / (frames - 1)
            root_loc.z = max(0, 0.25 * (1 - u * 2.5))
            sq = math.sin(min(1, max(0, (u - 0.35) / 0.65)) * math.pi)
            body_s = [1 + 0.1 * sq, 1 + 0.1 * sq, 1 - 0.14 * sq]
        elif state == "turn":            # turns to the right to walk away
            u = math.sin(f / (frames - 1) * math.pi / 2)
            root_rot = [0, 0, FACING_RIGHT * u]
        elif state == "hop":             # hopping in place (app slides the window), facing right
            root_rot = [0, 0, 75]
            u = f / frames
            root_loc.z = 0.22 * math.sin(u * math.pi)
            body_rot = [8 * math.sin(u * tau), 0, 0]
            for s in (-1, 1):
                leg_rot[s] = [30 * math.sin(u * tau), 0, 0]
            tail_rot = [-20, 0, 15 * math.sin(u * tau)]
        elif state == "sleep":
            slow = math.sin(t * tau)
            body_s = [1, 1, 1 + 0.025 * slow]
            head_rot = [14 + 2 * slow, 0, 8]
            tail_rot = [0, 0, 4 * slow]
            eye = 0.1

        lifts.append(round(root_loc.z, 4))
        key(R["root"], f + 1, loc=root_loc, rot=root_rot)
        key(R["body"], f + 1, loc=body_loc, rot=body_rot, scale=body_s)
        key(R["head"], f + 1, rot=head_rot)
        key(R["tail"], f + 1, rot=tail_rot)
        for s in (-1, 1):
            key(R[f"leg{s}"], f + 1, rot=leg_rot[s])
            key(R[f"ear{s}"], f + 1, rot=ear_rot[s])
            key(R[f"eye{s}"], f + 1, scale=[1, 1, eye])
            if f"bleg{s}" in R:
                key(R[f"bleg{s}"], f + 1, rot=bleg_rot[s])
        # Open/closed mouth (yawn, roar): swap the smile for the hidden open mouth.
        for o in bpy.context.scene.objects:
            if o.name.startswith("mouthopen_"):
                o.hide_render = not mouth_open
                o.keyframe_insert("hide_render", frame=f + 1)
            elif o.name.startswith("smile"):
                o.hide_render = mouth_open
                o.keyframe_insert("hide_render", frame=f + 1)
    return lifts

def write_manifest(outdir, state, lifts, size):
    """Where the paws meet the ground in the image, so the app can sit the lion on the taskbar edge."""
    from bpy_extras.object_utils import world_to_camera_view
    sc = bpy.context.scene
    sc.frame_set(1)
    bpy.context.view_layer.update()  # camera was re-aimed; refresh its matrix before projecting
    cam = sc.camera
    def px(co):
        v = world_to_camera_view(sc, cam, Vector(co))
        return v.x, 1 - v.y   # normalized, y down
    if state in STANDING_STATES:   # side-on body: paws nearest the camera are at y≈-0.3, body spans x≈-0.85..0.75
        cx, base_y = px((-0.05, -0.32, 0))
        lx, _ = px((-0.85, -0.2, 0))
        rx, _ = px((0.75, -0.2, 0))
        _, up_y = px((-0.05, -0.32, 0.25))
    else:
        cx, base_y = px((0, -0.48, 0))
        lx, _ = px((-0.6, -0.3, 0))
        rx, _ = px((0.6, -0.3, 0))
        _, up_y = px((0, -0.48, 0.25))
    frames, loop = STATES[state]
    manifest = {
        "state": state, "frames": frames, "fps": FPS, "loop": loop, "size": size,
        "baseline_y": round(base_y, 4),           # paw/ground line, fraction of image height
        "center_x": round(cx, 4),                 # body centre, fraction of image width
        "shadow_w": round(rx - lx, 4),            # shadow ellipse width, fraction of image width
        "lift_per_unit": round((base_y - up_y) / 0.25, 4),  # image-height fraction per world unit of lift
        "lift": lifts,                            # per-frame height above ground (world units)
    }
    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, f"{state}.json"), "w") as fh:
        json.dump(manifest, fh)

def setup_render(outdir, state, size):
    sc = bpy.context.scene
    frames, _ = STATES[state]
    sc.frame_start, sc.frame_end = 1, frames
    sc.render.fps = FPS
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.engine = "CYCLES"
    sc.cycles.samples = 24
    sc.cycles.use_denoising = True
    sc.render.use_persistent_data = True
    sc.render.filepath = os.path.join(outdir, state, "f_")
    # The ground contact shadow is drawn by the app (and the preview page) as a soft ellipse,
    # using the baseline + per-frame lift exported in the manifest.
    # Frame with headroom for hops/drag so no state gets cropped (frames are trimmed later).
    cam = sc.camera
    cam.data.lens = 52
    target = Vector((0, -0.05, 1.2))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = json.loads(argv[0]) if argv else {}
    p = dict(lion.DEFAULTS)
    with open(args.get("design", os.path.join(HERE, "..", "design", "chosen.json"))) as fh:
        p.update({k: v for k, v in json.load(fh).items() if k != "note"})
    p.update(args.get("overrides", {}))
    state = args["state"]
    if state in STANDING_STATES:
        p["pose"] = "standing"
    p["mouth_both"] = True   # hidden open mouth available for yawn/roar
    if state in ("jump", "tail_chase"):
        p["mane_core_small"] = True   # the back of the head is seen while spinning: keep it curly
    bpy.ops.wm.read_factory_settings(use_empty=True)
    lion.build_lion(p)
    lion.setup_scene(p)
    R = rig(p)
    lifts = animate(state, R)
    outdir = os.path.abspath(args["outdir"])
    size = args.get("size", 360)
    setup_render(outdir, state, size)
    write_manifest(outdir, state, lifts, size)
    if not args.get("manifest_only"):
        bpy.ops.render.render(animation=True)
    print("ANIMATED", state)

main()
