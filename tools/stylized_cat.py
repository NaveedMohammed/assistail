"""
Stylized (sculpted-cartoon) kitten: one smooth heart-shaped head mesh, huge layered eyes with liner, big ears with a
pink gradient, painted face markings (white blaze, forehead stripes), a fine brushed-fur bump, thin tinted whiskers,
and a small seated body. All procedural; inspired by stylized 3D kittens, no third-party assets.

Run headless:
  blender -b -P tools/stylized_cat.py -- options.json      (or a JSON string)

Part names follow the lion/cat rig conventions (head, ear, earin, eye, glint, nose, mouth, leg, paw, toe, tail, ...).
"""
import bpy, bmesh, json, math, os, random, sys, importlib.util
from mathutils import Vector, Euler

HERE = os.path.dirname(os.path.abspath(__file__))
def _load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)   # guarded: neither script renders on import
    return mod
cat = _load("procedural_cat")
lion = cat.lion
sphere, capsule, curve_obj, hex_rgba = lion.sphere, lion.capsule, lion.curve_obj, lion.hex_rgba

COATS = {
    "orange":  {"pattern": "tabby",  "base": "#EC8A34", "stripe": "#C85E16", "white": "#EED8B6",
                "iris": "#86AFCF", "nose": "#E2907C", "whisker": "#D9822B"},
    "grey":    {"pattern": "tabby",  "base": "#8C8987", "stripe": "#3C3837", "white": "#E8DCC6",
                "iris": "#5B4A32", "nose": "#B98C82", "whisker": "#8A5A4A", "pupil": 0.68},
    "calico":  {"pattern": "calico", "base": "#E58A35", "stripe": "#3A302C", "white": "#ECD4AE",
                "iris": "#A3B54A", "nose": "#E39A8C", "whisker": "#C98A5A"},
}

DEFAULTS = {
    "out": "stylized_cat.png", "size": 900, "seed": 5, "coat": "orange",
    "head_r": 0.72,          # head radius (the body stays small)
    "ear_scale": 1.15, "eye_scale": 1.0, "pupil": 0.55,
    "body_scale": 0.78, "tail_curl": True, "mouth": "smile",   # smile | open
    "paw_wrap": True, "ao": 0.4, "closeup": False,
    "cream": None,           # override the coat's light colour
    "outline": 0.0,          # toon outline thickness in scene units (0 = off; ~0.02 is about 1 px at taskbar size)
    "outline_only": None,    # list of part-name prefixes to outline, or None for the whole cat
    "camera": "three_quarter", "engine": "EEVEE", "standard_view": True,
}

# ------------------------------------------------------------------ node helpers

class NB:
    def __init__(self, mat):
        self.N, self.L = mat.node_tree.nodes, mat.node_tree.links
    def _in(self, sock, v):
        if isinstance(v, (int, float)):
            sock.default_value = v
        elif isinstance(v, tuple):
            sock.default_value = v
        else:
            self.L.new(v, sock)
    def math(self, op, a, b=0.0, clamp=False):
        n = self.N.new("ShaderNodeMath"); n.operation = op; n.use_clamp = clamp
        self._in(n.inputs[0], a); self._in(n.inputs[1], b)
        return n.outputs[0]
    def vmath(self, op, a, b=(0, 0, 0), scale=None):
        n = self.N.new("ShaderNodeVectorMath"); n.operation = op
        self._in(n.inputs[0], a); self._in(n.inputs[1], b)
        if scale is not None: n.inputs["Scale"].default_value = scale
        return n.outputs["Value"] if op in ("LENGTH", "DISTANCE", "DOT_PRODUCT") else n.outputs["Vector"]
    def sep(self, v):
        n = self.N.new("ShaderNodeSeparateXYZ"); self._in(n.inputs[0], v); return n.outputs
    def mix(self, fac, a, b):
        n = self.N.new("ShaderNodeMix"); n.data_type = "RGBA"
        self._in(n.inputs["Factor"], fac); self._in(n.inputs["A"], a); self._in(n.inputs["B"], b)
        return n.outputs["Result"]
    def ramp(self, fac, stops):
        r = self.N.new("ShaderNodeValToRGB"); els = r.color_ramp.elements
        while len(els) < len(stops): els.new(0.5)
        for e, (pos, col) in zip(els, stops):
            e.position = pos; e.color = col if isinstance(col, tuple) else hex_rgba(col)
        self._in(r.inputs["Fac"], fac); return r.outputs["Color"]
    def rest(self):
        a = self.N.new("ShaderNodeAttribute"); a.attribute_type = "GEOMETRY"; a.attribute_name = "rest"
        return a.outputs["Vector"]

def new_mat(name, rough=0.55):
    m = bpy.data.materials.new(name); m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Roughness"].default_value = rough
    return m, b

def fur_bump(nb, bsdf, rest, strength=0.18):
    """Fine brushed-fur detail: noise stretched along Z so it reads as strokes."""
    mp = nb.N.new("ShaderNodeMapping"); mp.inputs["Scale"].default_value = (90, 90, 22)
    nb._in(mp.inputs["Vector"], rest)
    n = nb.N.new("ShaderNodeTexNoise"); n.inputs["Scale"].default_value = 1.0; n.inputs["Detail"].default_value = 4
    nb._in(n.inputs["Vector"], mp.outputs["Vector"])
    bump = nb.N.new("ShaderNodeBump"); bump.inputs["Strength"].default_value = strength
    bump.inputs["Distance"].default_value = 0.004
    nb._in(bump.inputs["Height"], n.outputs["Fac"])
    nb._in(bsdf.inputs["Normal"], bump.outputs["Normal"])

def fur_material(p, name, region, hc, hr):
    """region: head | body | tail | ear | white."""
    c = dict(COATS[p["coat"]])
    if p.get("cream"):
        c["white"] = p["cream"]
    m, bsdf = new_mat(name, 0.6)
    if "Sheen Weight" in bsdf.inputs:
        bsdf.inputs["Sheen Weight"].default_value = 0.12
    if "Subsurface Weight" in bsdf.inputs:
        bsdf.inputs["Subsurface Weight"].default_value = 0.05
    nb = NB(m)
    rest = nb.rest()
    fur_bump(nb, bsdf, rest)
    white = hex_rgba(c["white"])
    if region == "white":
        bsdf.inputs["Base Color"].default_value = white
        return m
    X, Y, Z = nb.sep(rest)

    # Coat colour
    if c["pattern"] == "calico":
        n = nb.N.new("ShaderNodeTexNoise"); n.inputs["Scale"].default_value = 1.3; n.inputs["Detail"].default_value = 1.0
        nb._in(n.inputs["Vector"], nb.vmath("ADD", rest, (4.2, 1.3, 7.7)))
        col = nb.ramp(n.outputs["Fac"], [(0.47, c["base"]), (0.53, c["stripe"])])
    else:
        w = nb.N.new("ShaderNodeTexWave"); w.wave_type = "BANDS"
        if region == "head":   # vertical stripes on the forehead and crown
            w.bands_direction = "X"
            nb._in(w.inputs["Vector"], nb.vmath("SCALE", nb.vmath("SUBTRACT", rest, tuple(hc)), scale=1 / hr))
            w.inputs["Scale"].default_value = 1.9; w.inputs["Distortion"].default_value = 1.2
        else:                  # rings round the body, legs and tail
            w.bands_direction = "Z"
            nb._in(w.inputs["Vector"], rest)
            w.inputs["Scale"].default_value = 2.4; w.inputs["Distortion"].default_value = 2.0
        w.inputs["Detail"].default_value = 1.0
        stripe = nb.math("SUBTRACT", 1.0, nb.ramp(w.outputs["Fac"], [(0.24, (0, 0, 0, 1)), (0.34, (1, 1, 1, 1))]))
        if region == "head":   # only on the top of the head
            zr = nb.math("DIVIDE", nb.math("SUBTRACT", Z, hc.z), hr)
            stripe = nb.math("MULTIPLY", stripe, nb.math("MULTIPLY", nb.math("SUBTRACT", zr, 0.28), 6.0, clamp=True))
        col = nb.mix(stripe, c["base"] and hex_rgba(c["base"]), hex_rgba(c["stripe"]))

    # White areas
    if region == "head":
        xr = nb.math("ABSOLUTE", nb.math("DIVIDE", nb.math("SUBTRACT", X, hc.x), hr))
        yr = nb.math("DIVIDE", nb.math("SUBTRACT", Y, hc.y), hr)
        zr = nb.math("DIVIDE", nb.math("SUBTRACT", Z, hc.z), hr)
        front = nb.math("MULTIPLY", nb.math("SUBTRACT", nb.math("MULTIPLY", yr, -1.0), 0.3), 6.0, clamp=True)
        width = nb.math("ADD", nb.math("MAXIMUM", nb.math("SUBTRACT", 0.22, zr), 0.0), 0.1)   # blaze widens downward
        inside = nb.math("MULTIPLY", nb.math("SUBTRACT", width, xr), 22.0, clamp=True)
        chin = nb.math("MULTIPLY", nb.math("SUBTRACT", nb.math("MULTIPLY", zr, -1.0), 0.5), 8.0, clamp=True)
        W = nb.math("MAXIMUM", nb.math("MULTIPLY", front, inside), chin)
    elif region == "body":
        fw = nb.math("MULTIPLY", nb.math("SUBTRACT", nb.math("MULTIPLY", Y, -1.0), 0.02), 7.0, clamp=True)   # chest
        low = nb.math("MULTIPLY", nb.math("SUBTRACT", 0.22, Z), 8.0, clamp=True)                              # feet
        W = nb.math("MAXIMUM", fw, low)
    else:
        W = 0.0
    nb._in(bsdf.inputs["Base Color"], nb.mix(W, col, white))
    return m

def inner_ear_material():
    m, bsdf = new_mat("ear_inner", 0.7)
    nb = NB(m)
    tc = nb.N.new("ShaderNodeTexCoord")
    _, _, gz = nb.sep(tc.outputs["Generated"])
    nb._in(bsdf.inputs["Base Color"], nb.ramp(gz, [(0.1, "#B8606A"), (0.9, "#EFA9A4")]))
    return m

def eye_material(p):
    c = COATS[p["coat"]]
    m, bsdf = new_mat("eye", 0.04)
    if "Coat Weight" in bsdf.inputs:
        bsdf.inputs["Coat Weight"].default_value = 1.0
    nb = NB(m)
    tc = nb.N.new("ShaderNodeTexCoord")
    x, _, z = nb.sep(tc.outputs["Object"])
    comb = nb.N.new("ShaderNodeCombineXYZ"); nb._in(comb.inputs[0], x); nb._in(comb.inputs[2], z)
    r = nb.vmath("LENGTH", comb.outputs[0])
    pr = c.get("pupil", p["pupil"])
    nb._in(bsdf.inputs["Base Color"], nb.ramp(r, [
        (pr - 0.02, "#120E14"), (pr + 0.03, c["iris"]), (0.78, c["iris"]), (0.8, "#1C222C"), (0.86, "#1C222C"), (0.88, "#EEF2F5")]))
    return m

def plain(name, col, rough=0.5, emit=0.0):
    m, b = new_mat(name, rough)
    b.inputs["Base Color"].default_value = hex_rgba(col)
    if emit:
        b.inputs["Emission Color"].default_value = hex_rgba(col)
        b.inputs["Emission Strength"].default_value = emit
    return m

# ------------------------------------------------------------------ head

def heart_head(hc, hr, mat):
    """One smooth head: wide cheeks, small chin, slightly flat top, little muzzle bump."""
    bpy.ops.mesh.primitive_uv_sphere_add(segments=64, ring_count=32, location=hc)
    o = bpy.context.object; o.name = "head"
    for v in o.data.vertices:
        x, y, z = v.co
        cheek = math.exp(-((z + 0.1) / 0.45) ** 2)
        x *= 1 + 0.1 * cheek
        if z < -0.25:                                  # taper to a small chin
            t = (-z - 0.25) / 0.75
            x *= 1 - 0.26 * t ** 1.6
            y *= 1 - 0.12 * t
        if y < 0:                                      # muzzle
            y -= 0.09 * math.exp(-(x * x / 0.05 + (z + 0.4) ** 2 / 0.03))
        v.co = Vector((x * 1.0, y * 0.92, z * (1.02 if z > 0 else 0.96))) * hr
    o.data.update()
    o.data.materials.append(mat)
    lion.smooth(o, 1)
    return o

def front_point(head, hc, x, z, push=0.0):
    """World point on the head's front surface at local (x, z)."""
    ok, loc, n, _ = head.ray_cast(Vector((x, -10, z)), Vector((0, 1, 0)))
    if not ok:
        return hc + Vector((x, 0, z))
    return hc + loc + Vector((0, -push, 0))

def top_point(head, hc, x, y):
    ok, loc, n, _ = head.ray_cast(Vector((x, y, 10)), Vector((0, 0, -1)))
    return hc + (loc if ok else Vector((x, y, 0)))

def tri_prism(name, loc, w, h, d, mat):
    """Soft rounded triangle (point down), for the nose."""
    me = bpy.data.meshes.new(name)
    vs = []
    for yy in (-d / 2, d / 2):
        vs += [(-w, yy, h / 2), (w, yy, h / 2), (0, yy, -h / 2)]
    me.from_pydata(vs, [], [(0, 2, 1), (3, 4, 5), (0, 1, 4, 3), (1, 2, 5, 4), (2, 0, 3, 5)])
    o = bpy.data.objects.new(name, me); bpy.context.collection.objects.link(o)
    o.location = loc
    me.materials.append(mat)
    lion.smooth(o, 3)
    return o

def build_seated_body(p, F, pads):
    bs = p["body_scale"]
    # Body (small, seated)
    sphere("body", (0, 0.05, 0.58), (0.46 * bs, 0.44 * bs, 0.58), F["body"])
    sphere("belly", (0, -0.2 * bs, 0.58), (0.26 * bs, 0.17, 0.4), F["white"])
    for s in (-1, 1):
        sphere(f"haunch{s}", (0.32 * s * bs, 0.1, 0.28), (0.25 * bs, 0.37 * bs, 0.26), F["body"])
        sphere(f"bfoot{s}", (0.36 * s * bs, -0.28 * bs, 0.08), (0.12, 0.2, 0.08), F["white"])
        lx = 0.15 * s
        # Front legs stand in front of the chest so they read as two columns
        top, bot, paw = (lx, -0.36 * bs, 0.66), (lx, -0.44 * bs, 0.12), (lx, -0.5 * bs, 0.075)
        capsule(f"leg{s}", top, bot, 0.085, F["body"])
        sphere(f"paw{s}", paw, (0.11, 0.14, 0.08), F["white"])
        if p["paw_wrap"] and s == 1:   # the cat's left paw (+X) faces the 3/4 camera
            cat.paw_wrap(s, top, bot, 0.085, paw, (0.11, 0.14, 0.08), band_z=0.26)
            continue
        for t in (-1, 0, 1):
            sphere(f"toe{s}{t}", (lx + 0.04 * t, paw[1] - 0.13, 0.095), (0.009, 0.007, 0.022), pads, seg=8)
    tail = [(0.26, 0.4, 0.16), (0.62, 0.46, 0.09), (0.92, 0.26, 0.13), (1.02, 0.02, 0.38), (1.0, -0.04, 0.68),
            (0.94, -0.06, 0.9), (0.8, -0.06, 0.96), (0.73, -0.06, 0.86), (0.8, -0.06, 0.78)]
    if not p["tail_curl"]:
        tail = tail[:6] + [(0.92, -0.06, 1.08)]
    t = curve_obj("tail", [(x * bs, y * bs, z) for x, y, z in tail], 0.06, F["tail"], resolution=16)
    for i, pt in enumerate(t.data.splines[0].points):
        pt.radius = 1.0 - 0.35 * i / (len(tail) - 1)


def build_standing_body(p, F, pads):
    """On all fours, facing -Y (front legs at y=-0.28, back legs at y=+0.45), for walking and chasing."""
    sphere("body", (0, 0.1, 0.5), (0.3, 0.5, 0.27), F["body"])
    sphere("chest", (0, -0.25, 0.55), (0.29, 0.27, 0.3), F["body"])
    sphere("rump", (0, 0.45, 0.52), (0.3, 0.25, 0.27), F["body"])
    sphere("belly", (0, 0.02, 0.36), (0.21, 0.36, 0.12), F["white"])
    for s in (-1, 1):
        x = 0.15 * s
        for pre, y in (("leg", -0.28), ("bleg", 0.45)):
            top, bot, paw = (x, y, 0.5), (x, y, 0.11), (x, y - 0.04, 0.065)
            capsule(f"{pre}{s}", top, bot, 0.075, F["body"])
            pw = "paw" if pre == "leg" else "bpaw"
            sphere(f"{pw}{s}", paw, (0.1, 0.12, 0.07), F["white"])
            if pre == "leg" and p["paw_wrap"] and s == 1:
                cat.paw_wrap(s, top, bot, 0.075, paw, (0.1, 0.12, 0.07), band_z=0.24)
                continue
            for t in (-1, 0, 1):
                sphere(f"{'toe' if pre == 'leg' else 'btoe'}{s}{t}", (x + 0.035 * t, y - 0.15, 0.08), (0.008, 0.006, 0.02), pads, seg=8)
    tail = [(0, 0.66, 0.56), (0, 0.82, 0.72), (0, 0.9, 0.98), (0, 0.84, 1.18), (0, 0.72, 1.22), (0, 0.67, 1.12), (0, 0.72, 1.04)]
    t = curve_obj("tail", tail, 0.06, F["tail"], resolution=16)
    for i, pt in enumerate(t.data.splines[0].points):
        pt.radius = 1.0 - 0.35 * i / (len(tail) - 1)

def build(p):
    rng = random.Random(p["seed"])
    c = COATS[p["coat"]]
    hr, bs = p["head_r"], p["body_scale"]
    hc = Vector((0, -0.55, 0.78 + hr * 0.62)) if p.get("pose") == "standing" else Vector((0, -0.12, 0.98 + hr * 0.72))
    F = {r: fur_material(p, f"fur_{r}", r, hc, hr) for r in ("head", "body", "tail", "ear", "white")}
    dark = plain("mouth_line", "#5E302A", 0.5)
    whisk = plain("whisker", c["whisker"], 0.5)
    pads = plain("pads", "#E7A79E", 0.6)
    liner = plain("liner", "#231A1C", 0.35)
    glint = plain("glint", "#FFFFFF", 0.1, emit=2.0)

    if p.get("pose") == "standing":
        build_standing_body(p, F, pads)
    else:
        build_seated_body(p, F, pads)

    # Head
    head = heart_head(hc, hr, F["head"])
    bpy.context.view_layer.update()
    for k in range(5):   # little tuft on the crown
        base = top_point(head, hc, (k - 2) * 0.035, 0.02) - Vector((0, 0, 0.02))
        d = Vector(((k - 2) * 0.25 + rng.uniform(-0.1, 0.1), rng.uniform(-0.2, 0.2), 1)).normalized()
        bpy.ops.mesh.primitive_cone_add(vertices=8, radius1=0.022, radius2=0, depth=0.12 + rng.uniform(0, 0.04), location=base + d * 0.05)
        o = bpy.context.object; o.name = f"head_tuft{k}"
        o.rotation_mode = "QUATERNION"; o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
        o.data.materials.append(F["head"]); lion.smooth(o, 1)

    # Ears: big, flattened, tilted out, with a pink gradient inside
    es = p["ear_scale"]
    inner = inner_ear_material()
    for s in (-1, 1):
        root = top_point(head, hc, 0.5 * hr * s, 0.02)
        rot = Euler((math.radians(-8), math.radians(20) * s, 0))
        R = rot.to_matrix()
        sx, sy, sz = 0.42 * hr * es, 0.13 * hr * es, 0.42 * hr * es
        bpy.ops.mesh.primitive_cone_add(vertices=32, radius1=1, radius2=0.1, depth=2, location=root + R @ Vector((0, 0, sz - 0.14 * hr)))
        o = bpy.context.object; o.name = f"ear{s}"; o.scale = (sx, sy, sz); o.rotation_euler = rot
        o.data.materials.append(F["ear"]); lion.smooth(o, 2)
        bpy.ops.mesh.primitive_cone_add(vertices=32, radius1=1, radius2=0.1, depth=2,
                                        location=root + R @ Vector((0, -0.075 * hr * es, sz - 0.18 * hr)))
        o = bpy.context.object; o.name = f"earin{s}"; o.scale = (sx * 0.72, sy * 0.5, sz * 0.82); o.rotation_euler = rot
        o.data.materials.append(inner); lion.smooth(o, 2)

    # Eyes: glossy painted eyeball (pupil, iris, dark ring, white) + liner rim + highlights
    eyem = eye_material(p)
    re = 0.28 * hr * p["eye_scale"]
    for s in (-1, 1):
        surf = front_point(head, hc, 0.37 * hr * s, -0.02 * hr)
        ec = surf + Vector((0, re * 0.3, 0))
        sphere(f"eye{s}", ec, (re * 0.92, re * 0.6, re), eyem, seg=32)
        sphere(f"eye{s}_liner", ec + Vector((0, 0.025, 0.004)), (re * 1.06, re * 0.58, re * 1.08), liner, seg=32)
        front = ec - Vector((0, re * 0.58, 0))
        sphere(f"glint{s}", front + Vector((0.32 * re * s, -0.02, 0.36 * re)), (re * 0.2, re * 0.08, re * 0.2), glint, seg=16)
        sphere(f"glint2{s}", front + Vector((-0.3 * re * s, -0.01, -0.34 * re)), (re * 0.09, re * 0.04, re * 0.09), glint, seg=12)

    # Nose, mouth, whiskers on the muzzle surface
    nose_pt = front_point(head, hc, 0, -0.27 * hr, push=0.005)
    tri_prism("nose", nose_pt, 0.16 * hr, 0.14 * hr, 0.08 * hr, plain("nose", c["nose"], 0.4))
    def face_pts(pts, push=0.004):
        return [tuple(front_point(head, hc, x * hr, z * hr, push)) for x, z in pts]
    if p["mouth"] == "open":
        sphere("mouth_open", front_point(head, hc, 0, -0.47 * hr, -0.03), (0.15 * hr, 0.07 * hr, 0.13 * hr), plain("mouth_in", "#7A2E2E", 0.5))
        sphere("tongue", front_point(head, hc, 0, -0.52 * hr, -0.01), (0.09 * hr, 0.05 * hr, 0.05 * hr), plain("tongue", "#E58A8A", 0.5))
    for s in (-1, 1):
        curve_obj(f"mouth{s}", face_pts([(0, -0.33), (0.01 * s, -0.4), (0.07 * s, -0.44), (0.16 * s, -0.42), (0.23 * s, -0.36)]), 0.008 * hr, dark)
        for k in range(3):
            a = front_point(head, hc, 0.2 * hr * s, (-0.3 - 0.05 * k) * hr, -0.01)
            droop = (-0.02 + 0.06 * k) * hr
            pts = [tuple(a), tuple(a + Vector((0.35 * hr * s, 0.05 * hr, -droop * 0.3))),
                   tuple(a + Vector((0.72 * hr * s, 0.16 * hr, -droop)))]
            curve_obj(f"whisker{s}{k}", pts, 0.0045, whisk)

    if p.get("anim_parts"):
        build_anim_parts(p, head, hc, hr, F)
    cat.bake_rest_positions({m.name for m in F.values()})
    if p["ao"]:
        cat.add_ao(list(F.values()), p["ao"])
    if p["outline"]:
        cat.add_outline(p["outline"], only=tuple(p["outline_only"]) if p["outline_only"] else None)

def build_anim_parts(p, head, hc, hr, F):
    """Hidden parts that animate_cat.py shows per frame: open mouth, a long licking tongue, and the cloth lying loose
    on the ground after he pulls it off."""
    from mathutils import Matrix
    cavity = plain("mouth_in", "#6E2A2C", 0.5)
    tongue = plain("tongue", "#E8848A", 0.45)
    parts = [
        sphere("mouthopen_cavity", front_point(head, hc, 0, -0.46 * hr, -0.035), (0.14 * hr, 0.07 * hr, 0.12 * hr), cavity),
        sphere("mouthopen_tongue", front_point(head, hc, 0, -0.5 * hr, -0.02), (0.085 * hr, 0.05 * hr, 0.045 * hr), tongue),
    ]
    # Licking tongue: origin at its root so scaling Y pushes it out of the mouth
    bpy.ops.mesh.primitive_uv_sphere_add(segments=24, ring_count=12)
    t = bpy.context.object; t.name = "lick_tongue"
    t.data.transform(Matrix.Translation((0, -1, 0)))
    t.location = front_point(head, hc, 0, -0.46 * hr, -0.04)
    t.scale = (0.12 * hr, 0.27 * hr, 0.06 * hr)   # a soft paddle, not a needle
    t.rotation_euler = (math.radians(-25), 0, 0)
    t.data.materials.append(tongue); lion.smooth(t, 1)
    parts.append(t)
    # Loose cloth on the ground (crumpled square + its knot)
    cloth = cat.tartan_material()
    # About twice the paw-wrap size so it reads on the taskbar; a loose heap with a fold and the knot on top
    parts.append(sphere("cloth_dropped", (0, 0, 0.04), (0.3, 0.2, 0.045), cloth, seg=32))
    parts.append(sphere("cloth_dropped_fold", (0.08, 0.04, 0.09), (0.15, 0.11, 0.05), cloth, seg=24))
    parts.append(sphere("cloth_dropped_knot", (-0.12, -0.04, 0.1), (0.07, 0.06, 0.055), cloth, seg=20))
    parts.append(sphere("cloth_dropped_end", (-0.24, -0.1, 0.05), (0.09, 0.05, 0.02), cloth, seg=16))
    for o in parts:
        o.hide_render = True

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = dict(DEFAULTS)
    if argv:
        p.update(json.load(open(argv[0])) if argv[0].endswith(".json") else json.loads(argv[0]))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    build(p)
    lion.setup_scene(p)
    cat.frame_camera()
    if p["closeup"]:
        cat.closeup_camera((0.16, -0.28, 0.22))
    bpy.context.scene.render.filepath = p["out"]
    bpy.ops.render.render(write_still=True)
    print("RENDERED", p["out"])

if __name__ == "__main__":
    main()
