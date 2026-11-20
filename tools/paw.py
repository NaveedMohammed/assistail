"""
B5 level 4/5 reminders: a big lion paw (forearm + paw) in the same style as the procedural lion.
Renders one still for design review (animations come later, after a look is approved).

  blender -b -P tools/paw.py -- '{"out": "design/paw/p1.png", "view": "pads", "pad_color": "#C98B7A"}'

Views:
  pads  - palm toward the camera (the "boop" / level 5): pads squish on the glass
  sign  - the paw comes in from the right and grips the edge of a blank sign (level 4); the app draws the text
"""
import bpy, json, math, os, random, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("procedural_lion", os.path.join(HERE, "procedural_lion.py"))
lion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lion)  # guarded: doesn't render on import

DEFAULTS = {
    "out": "paw.png",
    "size": 700,
    "seed": 3,
    "design": os.path.join(HERE, "..", "design", "chosen.json"),   # lion colours
    "view": "pads",              # pads | sign
    "pad_color": "#C98B7A",      # pink-brown beans
    "pad_shape": "round",        # round | heart
    "toe_beans": True,           # little pads on each toe
    "wrist_curls": True,         # a ring of mane curls around the wrist
    "squish": 0.0,               # 0..1: pads flattened against the glass (boop)
    "sign_style": "card",        # card | wood
    "engine": "EEVEE",
}


def build_paw(p, mats, rng):
    """Paw pointing up (+Z), palm facing -Y (the camera), wrist at z=0. Returns the objects."""
    fur, light, pad, mane_a, mane_b = mats
    objs = []
    sq = p["squish"]
    flat = 1 - 0.45 * sq                                  # palm flattens toward the camera
    wide = 1 + 0.12 * sq

    # Forearm: two overlapping soft ovals (thicker near the paw), smooth like the lion's legs.
    objs.append(lion.sphere("arm", (0, 0.04, -1.25), (0.4, 0.38, 1.35), fur, seg=32))
    objs.append(lion.sphere("arm_top", (0, 0.02, -0.3), (0.39, 0.36, 0.45), fur, seg=32))
    objs.append(lion.sphere("mitt", (0, 0, 0.28), (0.5 * wide, 0.32 * flat, 0.44 * wide), light))
    toes = [(-0.34, 0.55), (-0.12, 0.69), (0.12, 0.69), (0.34, 0.55)]
    for i, (x, z) in enumerate(toes):
        objs.append(lion.sphere(f"toe{i}", (x * wide, -0.02, z * wide), (0.16 * wide, 0.17 * flat, 0.16 * wide), light))

    # Pads on the palm side (-Y). Squished pads spread out and get a touch shinier (pressed on glass).
    front = -0.3 * flat
    if p["pad_shape"] == "heart":
        for s in (-1, 1):
            objs.append(lion.sphere(f"pad_lobe{s}", (0.09 * s * wide, front, 0.24), (0.15 * wide, 0.06, 0.13 * wide), pad))
        objs.append(lion.sphere("pad_tip", (0, front + 0.01, 0.11), (0.13 * wide, 0.06, 0.12 * wide), pad))
    else:
        objs.append(lion.sphere("pad_main", (0, front, 0.19), (0.26 * wide, 0.06, 0.19 * wide), pad))
    if p["toe_beans"]:
        for i, (x, z) in enumerate(toes):
            objs.append(lion.sphere(f"bean{i}", (x * wide, -0.17 * flat - 0.02, z * wide + 0.01), (0.09 * wide, 0.04, 0.1 * wide), pad))

    if p["wrist_curls"]:
        # Same curly style as the mane, pointing outward around the wrist.
        for i in range(34):
            a = i / 26 * math.tau + rng.uniform(-0.1, 0.1)
            d = Vector((math.cos(a), math.sin(a) * 0.8, rng.uniform(-0.25, 0.35))).normalized()
            base = Vector((0, 0, -0.08 + rng.uniform(-0.05, 0.05))) + Vector((math.cos(a) * 0.36, math.sin(a) * 0.33, 0))
            o = lion.curve_obj(f"curl_w{i}", lion.curl_points("poofy", 0.7, rng), 0.04, mane_a if i % 3 else mane_b)
            o.rotation_mode = "QUATERNION"
            o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
            o.location = base
            objs.append(o)
    return objs


def build_sign(p):
    """A blank sign, facing the camera, to the left of the paw."""
    if p["sign_style"] == "wood":
        board = lion.material("sign_wood", "#C99A63", 0.7)
        edge = lion.material("sign_edge", "#8A5E34", 0.7)
    else:
        board = lion.material("sign_card", "#FFF8EC", 0.5)
        edge = lion.material("sign_edge", "#E6D3B3", 0.6)
    bpy.ops.mesh.primitive_cube_add(location=(-1.2, 0.05, 0.25))
    o = bpy.context.object
    o.name = "sign"
    o.scale = (1.25, 0.05, 0.75)
    o.data.materials.append(board)
    bev = o.modifiers.new("bevel", "BEVEL")
    bev.width = 0.12
    bev.segments = 8
    for poly in o.data.polygons:
        poly.use_smooth = True
    # A thin rim so it reads as a card or board at small sizes.
    bpy.ops.mesh.primitive_cube_add(location=(-1.2, 0.1, 0.25))
    r = bpy.context.object
    r.name = "sign_rim"
    r.scale = (1.31, 0.04, 0.81)
    r.data.materials.append(edge)
    b2 = r.modifiers.new("bevel", "BEVEL")
    b2.width = 0.14
    b2.segments = 8
    return o


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = dict(DEFAULTS)
    cli = json.loads(argv[0]) if argv else {}
    design_path = cli.get("design", p["design"])
    design = {}
    if os.path.exists(design_path):
        with open(design_path, encoding="utf-8") as f:
            design = json.load(f)
    p.update({k: v for k, v in design.get("paw", {}).items() if k in DEFAULTS})   # the approved paw look
    p.update(cli)
    look = dict(lion.DEFAULTS)
    look.update(design)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    rng = random.Random(p["seed"])
    mats = (
        lion.material("fur", look["fur"], 0.6, sss=0.08),
        lion.material("fur_light", look["fur_light"], 0.6, sss=0.08),
        lion.material("pad", p["pad_color"], 0.45 - 0.25 * p["squish"], sss=0.15, coat=0.2 + 0.5 * p["squish"]),
        lion.material("mane", look["mane"], 0.7),
        lion.material("mane_tip", look["mane_tip"], 0.7),
    )
    paw = build_paw(p, mats, rng)

    # Group the paw under an empty so the sign view can turn it as one piece (everything built so far).
    root = bpy.data.objects.new("paw_root", None)
    bpy.context.collection.objects.link(root)
    for o in list(bpy.context.scene.objects):
        if o is not root and o.parent is None and o.type in {"MESH", "CURVE"}:
            o.parent = root

    look["size"] = p["size"]
    look["engine"] = p["engine"]
    lion.setup_scene(look)
    cam = bpy.context.scene.camera
    if p["view"] == "sign":
        build_sign(p)
        # Arm comes in from the right; the paw's back faces the camera, toes curl over the sign's right edge.
        root.rotation_euler = (0, math.radians(90), math.radians(180))
        root.location = (0.55, -0.12, 0.35)
        cam.location = (-0.5, -7.5, 0.5)
        target = Vector((-0.45, 0, 0.3))
    else:
        cam.location = (0, -4.6, 0.35)
        target = Vector((0, 0, -0.05))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()

    out = p["out"]
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    bpy.context.scene.render.filepath = os.path.abspath(out)
    bpy.ops.render.render(write_still=True)
    print("RENDERED", out)


if __name__ == "__main__":
    main()
