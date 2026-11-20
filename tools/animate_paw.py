"""
B5 levels 4 and 5: renders the paw animations as transparent PNG frames + a JSON with what the app needs.

  blender -b -P tools/animate_paw.py -- '{"anim": "boop", "outdir": "design/anim"}'
  blender -b -P tools/animate_paw.py -- '{"anim": "sign", "outdir": "design/anim"}'

boop  -> <outdir>/paw_boop/f_####.png   rise from below, wind up, press on the glass (squish), hold, pull back down
         <outdir>/paw_boop/print.png    the paw print the pads leave (flat colour, same framing as the press)
sign  -> <outdir>/paw_sign_in/f_####.png      the arm slides in from the right holding the board (the app plays it
                                              backwards to slide out)
         <outdir>/paw_sign_wiggle/f_####.png  a little wiggle, starting and ending at rest
Each folder gets <name>.json: frame count, fps, and for the sign the board's text area (fractions of the frame).
"""
import bpy, json, math, os, random, sys, importlib.util
from mathutils import Vector
from bpy_extras.object_utils import world_to_camera_view

HERE = os.path.dirname(os.path.abspath(__file__))


def load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


lion = load("procedural_lion")
paw = load("paw")

FPS = 15
DEFAULTS = {"anim": "boop", "outdir": "design/anim", "size": 512, "design": os.path.join(HERE, "..", "design", "chosen.json")}


def ease(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)


def build(p, view):
    """Builds the approved paw (and the sign) and the scene; returns (root, camera, pad material, objects by name)."""
    design = json.load(open(p["design"], encoding="utf-8")) if os.path.exists(p["design"]) else {}
    look = dict(lion.DEFAULTS)
    look.update(design)
    pp = dict(paw.DEFAULTS)
    pp.update({k: v for k, v in design.get("paw", {}).items() if k in paw.DEFAULTS})
    pp["view"] = view

    bpy.ops.wm.read_factory_settings(use_empty=True)
    rng = random.Random(pp["seed"])
    pad = lion.material("pad", pp["pad_color"], 0.45, sss=0.15, coat=0.2)
    mats = (lion.material("fur", look["fur"], 0.6, sss=0.08), lion.material("fur_light", look["fur_light"], 0.6, sss=0.08),
            pad, lion.material("mane", look["mane"], 0.7), lion.material("mane_tip", look["mane_tip"], 0.7))
    paw.build_paw(pp, mats, rng)
    root = bpy.data.objects.new("paw_root", None)
    bpy.context.collection.objects.link(root)
    for o in list(bpy.context.scene.objects):
        if o is not root and o.parent is None and o.type in {"MESH", "CURVE"}:
            o.parent = root
    look["size"] = p["size"]
    look["engine"] = "EEVEE"
    lion.setup_scene(look)
    sign = paw.build_sign(pp) if view == "sign" else None
    return root, bpy.context.scene.camera, pad, sign


def aim(cam, loc, target):
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()


def render(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.context.scene.render.filepath = os.path.abspath(path)
    bpy.ops.render.render(write_still=True)


def write_json(folder, name, data):
    with open(os.path.join(folder, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)


# ---------------------------------------------------------------------------------------------- boop

BOOP_FRAMES = 42


def boop_pose(f):
    """Root transform for frame f: (z, y toward the camera, squish 0..1)."""
    t = f / FPS
    if t < 0.6:                       # rise from below the frame
        z, y, s = -2.3 + 2.3 * ease(t / 0.6), 0.0, 0.0
    elif t < 0.9:                     # wind up: lean back a little
        u = ease((t - 0.6) / 0.3)
        z, y, s = 0.05 * u, 0.25 * u, 0.0
    elif t < 1.05:                    # boop! quick move to the glass
        u = ease((t - 0.9) / 0.15)
        z, y, s = 0.05, 0.25 - 1.25 * u, 0.0
    elif t < 1.25:                    # squish on contact
        u = ease((t - 1.05) / 0.2)
        z, y, s = 0.05, -1.0, u
    elif t < 1.9:                     # hold, a tiny wobble
        z, y, s = 0.05 + 0.01 * math.sin((t - 1.25) * 20), -1.0, 1.0 - 0.06 * math.sin((t - 1.25) * 9)
    else:                             # peel off and slide back down
        u = ease((t - 1.9) / (BOOP_FRAMES / FPS - 1.9))
        z, y, s = 0.05 - 2.4 * u, -1.0 + 1.0 * u, max(0.0, 1.0 - 4 * u)
    return z, y, s


def apply_boop(root, pad, z, y, s):
    root.location = (0, y, z)
    root.scale = (1 + 0.14 * s, 1 - 0.42 * s, 1 + 0.08 * s)   # pads flatten and spread on the glass
    b = pad.node_tree.nodes["Principled BSDF"]
    b.inputs["Roughness"].default_value = 0.45 - 0.3 * s
    if "Coat Weight" in b.inputs:
        b.inputs["Coat Weight"].default_value = 0.2 + 0.6 * s


def render_boop(p):
    root, cam, pad, _ = build(p, "pads")
    aim(cam, (0, -5.6, 0.25), (0, 0, -0.05))
    folder = os.path.join(p["outdir"], "paw_boop")
    for f in range(BOOP_FRAMES):
        apply_boop(root, pad, *boop_pose(f))
        render(os.path.join(folder, f"f_{f + 1:04d}.png"))

    # The paw print: only the pads, flat colour, at the pressed pose (same camera, so it lines up).
    apply_boop(root, pad, 0.05, -1.0, 1.0)
    ink = bpy.data.materials.new("ink")
    ink.use_nodes = True
    nt = ink.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = lion.hex_rgba("#5B3A2C")
    nt.links.new(em.outputs[0], out.inputs[0])
    for o in bpy.context.scene.objects:
        if o.type not in {"MESH", "CURVE"}:
            continue
        if o.name.startswith(("pad_", "bean")):
            o.data.materials.clear()
            o.data.materials.append(ink)
        else:
            o.hide_render = True
    render(os.path.join(folder, "print.png"))
    write_json(folder, "paw_boop", {"frames": BOOP_FRAMES, "fps": FPS, "loop": False,
                                    "press_frame": int(1.05 * FPS) + 1, "release_frame": int(1.9 * FPS) + 1})


# ---------------------------------------------------------------------------------------------- sign

SIGN_IN_FRAMES = 12
SIGN_WIGGLE_FRAMES = 14
SIGN_REST = (0.55, -0.12, 0.35)


def render_sign(p):
    root, cam, _, sign = build(p, "sign")
    bpy.context.scene.render.resolution_y = p["size"] // 2   # wide frame: the board and arm are a horizontal band
    aim(cam, (-0.5, -7.5, 0.35), (-0.45, 0, 0.3))
    parts = [(o, tuple(o.location)) for o in bpy.context.scene.objects if o.name.startswith("sign")]
    # Pivot for the wiggle: the paw's grip at the board's right edge.
    pivot = Vector((0.0, 0.0, 0.3))
    for o, base in parts:
        o.rotation_mode = "XYZ"

    def turn(v, a):
        """Rotates point v around the grip pivot by world Ry(-a) (in the camera plane)."""
        rel = Vector(v) - pivot
        return pivot + Vector((rel.x * math.cos(a) - rel.z * math.sin(a), rel.y, rel.x * math.sin(a) + rel.z * math.cos(a)))

    def pose(dx, rot):
        # Board and paw turn together around the grip point; everything slides by dx.
        a = math.radians(rot)
        shift = Vector((dx, 0, 0))
        for o, base in parts:
            o.location = turn(Vector(base) + shift, a)
            o.rotation_euler = (0, -a, 0)
        root.location = turn(Vector(SIGN_REST) + shift, a)
        # root's base rotation is Rz(180)·Ry(90); a world Ry(-a) on top of it is Euler Y = 90 + a.
        root.rotation_euler = (0, math.radians(90) + a, math.radians(180))

    fin = os.path.join(p["outdir"], "paw_sign_in")
    for f in range(SIGN_IN_FRAMES):
        u = ease(f / (SIGN_IN_FRAMES - 1))
        pose(4.2 * (1 - u), 6 * (1 - u))      # slides in, settling from a slight tilt
        render(os.path.join(fin, f"f_{f + 1:04d}.png"))

    # Text area: the board's front face at rest, as fractions of the frame (x, y from the top-left, w, h).
    pose(0, 0)
    bpy.context.view_layer.update()
    board = sign
    sc = bpy.context.scene
    pts = [world_to_camera_view(sc, cam, board.matrix_world @ Vector((x, -1, z))) for x in (-1, 1) for z in (-1, 1)]
    xs = [v.x for v in pts]
    ys = [1 - v.y for v in pts]
    inset, grip = 0.08, 0.2           # keep the text clear of the paw gripping the right edge
    w, h = max(xs) - min(xs), max(ys) - min(ys)
    text = {"x": min(xs) + w * inset, "y": min(ys) + h * inset, "w": w * (1 - inset - grip), "h": h * (1 - 2 * inset)}
    write_json(fin, "paw_sign_in", {"frames": SIGN_IN_FRAMES, "fps": FPS, "loop": False, "text_area": text})

    fw = os.path.join(p["outdir"], "paw_sign_wiggle")
    for f in range(SIGN_WIGGLE_FRAMES):
        t = f / SIGN_WIGGLE_FRAMES
        pose(0, 5 * math.sin(t * math.tau * 2) * (1 - t))
        render(os.path.join(fw, f"f_{f + 1:04d}.png"))
    write_json(fw, "paw_sign_wiggle", {"frames": SIGN_WIGGLE_FRAMES, "fps": FPS, "loop": False, "text_area": text})


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = dict(DEFAULTS)
    if argv:
        p.update(json.loads(argv[0]))
    if p["anim"] == "sign":
        render_sign(p)
    else:
        render_boop(p)
    print("ANIM DONE", p["anim"])


if __name__ == "__main__":
    main()
