"""
Renders a small cartoon butterfly flapping its wings (loop) as transparent PNG frames.

  blender -b -P tools/butterfly.py -- '{"outdir": "design/anim", "size": 128}'
Output: <outdir>/butterfly/f_####.png + <outdir>/butterfly.json
"""
import bpy, json, math, os, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("procedural_lion", os.path.join(HERE, "procedural_lion.py"))
lion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lion)

FRAMES = 8

def wing(name, mat, spot_mat, side, fore):
    """A rounded wing made from a flattened sphere, pivoting on the body axis (Y)."""
    pivot = bpy.data.objects.new(f"pivot_{name}", None)
    bpy.context.collection.objects.link(pivot)
    sx = 0.55 if fore else 0.42
    sy = 0.38 if fore else 0.3
    off = Vector((side * sx * 0.95, -0.12 if fore else 0.22, 0))
    w = lion.sphere(name, off, (sx, sy, 0.025), mat, rot=(0, 0, math.radians(side * (-18 if fore else 22))))
    spot = lion.sphere(name + "_spot", off + Vector((side * sx * 0.35, -0.04, 0.03)), (sx * 0.28, sy * 0.3, 0.02), spot_mat)
    edge = lion.sphere(name + "_edge", off * 1.02 + Vector((0, 0, -0.005)), (sx * 1.04, sy * 1.06, 0.018), lion.material("edge", "#3A2A5C", 0.6))
    for o in (w, spot, edge):
        o.parent = pivot
    return pivot

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = json.loads(argv[0]) if argv else {}
    outdir = os.path.abspath(args.get("outdir", "design/anim"))
    size = args.get("size", 128)
    bpy.ops.wm.read_factory_settings(use_empty=True)

    fore = lion.material("fore", "#7B6CF6", 0.4, sss=0.1)      # lavender-blue
    hind = lion.material("hind", "#F28AC6", 0.4, sss=0.1)      # pink
    spot = lion.material("spot", "#FFF3B0", 0.4)
    body_m = lion.material("bbody", "#2B2140", 0.5)
    lion.capsule("body", (0, -0.35, 0), (0, 0.4, 0), 0.07, body_m)
    lion.sphere("bhead", (0, -0.45, 0.02), (0.1, 0.1, 0.1), body_m)
    for s in (-1, 1):
        lion.curve_obj(f"antenna{s}", [(0.03 * s, -0.5, 0.06), (0.1 * s, -0.68, 0.2), (0.16 * s, -0.78, 0.3)], 0.012, body_m)
        lion.sphere(f"antip{s}", (0.16 * s, -0.78, 0.3), (0.03, 0.03, 0.03), body_m)
    pivots = []
    for s in (-1, 1):
        pivots.append((wing(f"fw{s}", fore, spot, s, True), s))
        pivots.append((wing(f"hw{s}", hind, spot, s, False), s))

    for f in range(FRAMES):
        flap = math.sin(f / FRAMES * math.tau)          # -1..1
        for pv, s in pivots:
            pv.rotation_euler = (0, math.radians(-s * (10 + 60 * (flap + 1) / 2)), 0)
            pv.keyframe_insert("rotation_euler", frame=f + 1)

    sc = bpy.context.scene
    sc.frame_start, sc.frame_end = 1, FRAMES
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.film_transparent = True
    sc.render.engine = "CYCLES"
    sc.cycles.samples = 24
    sc.cycles.use_denoising = True
    sc.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("w"); world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.9
    sc.world = world
    for name, loc, energy in (("key", (-2, -3, 4), 300), ("fill", (3, -2, 2), 120)):
        ld = bpy.data.lights.new(name, "AREA"); ld.energy = energy; ld.size = 3
        lo = bpy.data.objects.new(name, ld); bpy.context.collection.objects.link(lo); lo.location = loc
        lo.rotation_euler = (Vector((0, 0, 0)) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    cam_d = bpy.data.cameras.new("cam"); cam_d.lens = 50
    cam = bpy.data.objects.new("cam", cam_d); bpy.context.collection.objects.link(cam)
    cam.location = (1.6, -3.4, 2.6)                     # 3/4 view from above-front; body points left→right of screen
    cam.rotation_euler = (Vector((0, 0, 0.05)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam
    # Point the butterfly's head toward screen-right so the app can mirror it for leftward flight.
    for o in bpy.context.scene.objects:
        if o.parent is None and o.type in ("MESH", "CURVE", "EMPTY") and o.name not in ("cam",):
            pass
    root = bpy.data.objects.new("broot", None); bpy.context.collection.objects.link(root)
    for o in list(bpy.context.scene.objects):
        if o is not root and o.parent is None and o.type in ("MESH", "CURVE", "EMPTY"):
            o.parent = root
    root.rotation_euler = (0, 0, math.radians(90))

    os.makedirs(os.path.join(outdir, "butterfly"), exist_ok=True)
    sc.render.filepath = os.path.join(outdir, "butterfly", "f_")
    with open(os.path.join(outdir, "butterfly.json"), "w") as fh:
        json.dump({"state": "butterfly", "frames": FRAMES, "fps": 15, "loop": True, "size": size,
                   "baseline_y": 0.5, "center_x": 0.5, "shadow_w": 0, "lift_per_unit": 0, "lift": [0] * FRAMES}, fh)
    bpy.ops.render.render(animation=True)
    print("BUTTERFLY done")

main()
