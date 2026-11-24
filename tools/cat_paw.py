"""
B5 level 4/5 reminders for the cat: a big calico paw (cream mitt, pink beans) built with tools/paw.py's shapes,
optionally wearing the plaid cloth. Renders one still for design review.

  blender -b -P tools/cat_paw.py -- options.json
  {"out": "design/cat-paw/p1-pads.png", "view": "pads" | "sign", "cloth": "none" | "band" | "mitten"}
"""
import bpy, json, math, os, random, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
def _load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod
paw = _load("paw")
scat = _load("stylized_cat")
cat = scat.cat
lion = scat.lion

DEFAULTS = {"out": "cat_paw.png", "size": 700, "view": "pads", "cloth": "band", "squish": 0.0,
            "pad_color": "#E9A1A6", "sign_style": "wood", "engine": "EEVEE", "seed": 3}

def cloth_band(z, radius, mats, side=1):
    """Plaid band round the wrist with a knot and two loose ends on the outer side."""
    cloth = mats
    bpy.ops.mesh.primitive_torus_add(major_radius=radius, minor_radius=0.075, location=(0, 0, z), major_segments=40, minor_segments=14)
    b = bpy.context.object; b.name = "wrap_band"; b.scale = (1, 0.9, 1.25); b.data.materials.append(cloth); lion.smooth(b, 1)
    knot = Vector((radius * 0.92 * side, -radius * 0.45, z))
    objs = [b, lion.sphere("wrap_knot", knot, (0.11, 0.1, 0.1), cloth, seg=24)]
    for k, d in enumerate((Vector((0.6 * side, -0.4, -0.75)), Vector((0.9 * side, -0.2, 0.3)))):
        d = d.normalized()
        bpy.ops.mesh.primitive_cone_add(vertices=16, radius1=0.13, radius2=0.06, depth=0.36, location=knot + d * 0.2)
        e = bpy.context.object; e.name = f"wrap_end{k}"; e.scale = (1, 0.35, 1)
        e.rotation_mode = "QUATERNION"; e.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
        e.data.materials.append(cloth); lion.smooth(e, 2)
        objs.append(e)
    return objs

def build_parts(p, look, rng):
    """Builds the calico paw (+ cloth per p["cloth"]) into the current scene. Returns the pad material."""
    hc, hr = Vector((0, 0, 5)), 0.7                  # (head markings unused on a paw)
    fur = scat.fur_material(look, "fur_body", "tail", hc, hr)   # coat colours without the white chest mask
    light = scat.fur_material(look, "fur_white", "white", hc, hr)
    pad = lion.material("pad", p["pad_color"], 0.45 - 0.25 * p.get("squish", 0), sss=0.15, coat=0.2 + 0.5 * p.get("squish", 0))
    pp = dict(paw.DEFAULTS); pp.update({"wrist_curls": False, "toe_beans": True, "pad_shape": "round", "squish": p.get("squish", 0)})
    paw.build_paw(pp, (fur, light, pad, fur, fur), rng)
    cloth = cat.tartan_material(repeats=0.9)
    if p["cloth"] in ("band", "mitten"):
        cloth_band(-0.12, 0.42, cloth)
    if p["cloth"] == "mitten":   # plaid over the whole paw (the beans are covered)
        lion.sphere("mitten", (0, 0.0, 0.33), (0.58, 0.36, 0.56), cloth, seg=40)
        lion.sphere("mitten_cuff", (0, 0.0, 0.0), (0.45, 0.4, 0.3), cloth, seg=32)
        for o in bpy.context.scene.objects:      # the beans are under the cloth (they'd poke through when squished)
            if o.name.startswith(("pad_", "bean")):
                o.hide_render = True
    cat.bake_rest_positions({fur.name, light.name})
    cat.add_ao([fur, light], 0.3)
    return pad

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = dict(DEFAULTS)
    if argv:
        p.update(json.load(open(argv[0])) if argv[0].endswith(".json") else json.loads(argv[0]))
    with open(os.path.join(HERE, "..", "design", "chosen.json"), encoding="utf-8") as fh:
        look = dict(scat.DEFAULTS); look.update(json.load(fh)["cat"])
    bpy.ops.wm.read_factory_settings(use_empty=True)
    build_parts(p, look, random.Random(p["seed"]))

    root = bpy.data.objects.new("paw_root", None)
    bpy.context.collection.objects.link(root)
    for o in list(bpy.context.scene.objects):
        if o is not root and o.parent is None and o.type in {"MESH", "CURVE"}:
            o.parent = root
    look["size"] = p["size"]; look["engine"] = p["engine"]; look["camera"] = "front"
    lion.setup_scene(look)
    cam = bpy.context.scene.camera
    if p["view"] == "sign":
        paw.build_sign({"sign_style": p["sign_style"]})
        root.rotation_euler = (0, math.radians(90), math.radians(180))
        root.location = (0.55, -0.12, 0.35)
        cam.location = (-0.5, -7.5, 0.5)
        target = Vector((-0.45, 0, 0.3))
    else:
        cam.location = (0, -4.6, 0.35)
        target = Vector((0, 0, -0.05))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    os.makedirs(os.path.dirname(os.path.abspath(p["out"])), exist_ok=True)
    bpy.context.scene.render.filepath = os.path.abspath(p["out"])
    bpy.ops.render.render(write_still=True)
    print("RENDERED", p["out"])

if __name__ == "__main__":
    main()
