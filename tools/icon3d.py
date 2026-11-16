"""
Renders 3D icon options: a glossy paw print and a close-up of the lion's face.
  blender -b -P tools/icon3d.py -- '{"kind": "paw3d" | "face", "out": "design/icons/paw-3d.png"}'
"""
import bpy, json, math, os, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("procedural_lion", os.path.join(HERE, "procedural_lion.py"))
lion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lion)

def setup(size, cam_loc, target, lens):
    sc = bpy.context.scene
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.film_transparent = True
    sc.render.engine = "CYCLES"
    sc.cycles.samples = 64
    sc.cycles.use_denoising = True
    sc.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("w"); world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.7
    sc.world = world
    for name, loc, energy, sz in (("key", (-2.5, -3.5, 4), 500, 3), ("fill", (3, -2.5, 1.5), 180, 4), ("rim", (1.5, 3, 3), 300, 2)):
        ld = bpy.data.lights.new(name, "AREA"); ld.energy = energy; ld.size = sz
        lo = bpy.data.objects.new(name, ld); bpy.context.collection.objects.link(lo); lo.location = loc
        lo.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    cd = bpy.data.cameras.new("cam"); cd.lens = lens
    cam = bpy.data.objects.new("cam", cd); bpy.context.collection.objects.link(cam)
    cam.location = cam_loc
    cam.rotation_euler = (Vector(target) - Vector(cam_loc)).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam

def paw3d():
    """A chunky, glossy paw print standing upright facing the camera (-Y)."""
    pad = lion.material("pad", "#E8833A", 0.25, sss=0.15, coat=0.6)
    def bean(name, x, z, sx, sz, rot=0):
        o = lion.sphere(name, (x, 0, z), (sx, 0.16, sz), pad, rot=(0, math.radians(rot), 0), seg=32)
        lion.smooth(o, 2)
    bean("main", 0, -0.25, 0.62, 0.48)
    bean("mainL", -0.32, -0.38, 0.36, 0.34)
    bean("mainR", 0.32, -0.38, 0.36, 0.34)
    for name, x, z, rot in (("t1", -0.78, 0.38, 25), ("t2", -0.3, 0.78, 8), ("t3", 0.3, 0.78, -8), ("t4", 0.78, 0.38, -25)):
        bean(name, x, z, 0.24, 0.3, rot)
    setup(256, (0.5, -4.4, 0.5), (0, 0, 0.12), 50)

def face():
    p = dict(lion.DEFAULTS)
    with open(os.path.join(HERE, "..", "design", "chosen.json")) as fh:
        p.update({k: v for k, v in json.load(fh).items() if k != "note"})
    p["mane_core_small"] = False
    lion.build_lion(p)
    hc = lion.head_center(p)
    setup(256, (1.0, -4.2, hc.z + 0.35), (0, -0.1, hc.z + 0.05), 75)

def main():
    args = json.loads(sys.argv[sys.argv.index("--") + 1]) if "--" in sys.argv else {}
    bpy.ops.wm.read_factory_settings(use_empty=True)
    {"paw3d": paw3d, "face": face}[args["kind"]]()
    bpy.context.scene.render.filepath = os.path.abspath(args["out"])
    bpy.ops.render.render(write_still=True)
    print("ICON", args["out"])

main()
