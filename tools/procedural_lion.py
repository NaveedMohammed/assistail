"""
Builds a cute cartoon lion cub (seated) with a procedural CURLY mane, and renders a still.

Run headless:
  blender -b -P tools/procedural_lion.py -- '{"out": "design/round-1/own-a.png", "curl_style": "ringlets"}'

All look parameters can be overridden in the JSON (see DEFAULTS).
"""
import bpy, bmesh, json, math, random, sys
from mathutils import Vector, Matrix

DEFAULTS = {
    "out": "lion.png",
    "size": 900,                 # render resolution (square)
    "seed": 7,
    "fur": "#E9A646",            # body fur
    "fur_light": "#F8DDB0",      # muzzle / belly / paws
    "mane": "#8E3F1E",           # mane color
    "mane_tip": "#C2632F",       # second mane tone mixed in
    "eye_scale": 1.0,
    "head_scale": 1.0,
    "curl_style": "ringlets",    # ringlets | poofy | spirals
    "curl_count": 240,
    "standard_view": True,
    "curl_size": 1.0,
    "mane_volume": 1.0,
    "camera": "three_quarter",   # three_quarter | front
    "engine": "EEVEE",           # EEVEE | CYCLES
}

def hex_rgba(h, a=1.0):
    h = h.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]
    return (*lin, a)

def material(name, color, rough=0.55, sss=0.0, spec=0.35, coat=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = hex_rgba(color)
    b.inputs["Roughness"].default_value = rough
    if "Subsurface Weight" in b.inputs:
        b.inputs["Subsurface Weight"].default_value = sss
        b.inputs["Subsurface Radius"].default_value = (0.3, 0.15, 0.08)
    if "Specular IOR Level" in b.inputs:
        b.inputs["Specular IOR Level"].default_value = spec
    if "Coat Weight" in b.inputs:
        b.inputs["Coat Weight"].default_value = coat
    return m

def smooth(obj, levels=2):
    for p in obj.data.polygons:
        p.use_smooth = True
    mod = obj.modifiers.new("sub", "SUBSURF")
    mod.levels = levels
    mod.render_levels = levels

def sphere(name, loc, scale, mat, rot=(0, 0, 0), seg=24):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=seg // 2, location=loc, rotation=rot)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    o.data.materials.append(mat)
    smooth(o, 1)
    return o

def capsule(name, a, b, radius, mat):
    """Cylinder with rounded ends between points a and b."""
    a, b = Vector(a), Vector(b)
    d = b - a
    bpy.ops.mesh.primitive_cylinder_add(vertices=20, radius=radius, depth=d.length, location=(a + b) / 2)
    o = bpy.context.object
    o.name = name
    o.rotation_mode = "QUATERNION"
    o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d.normalized())
    o.data.materials.append(mat)
    smooth(o, 2)
    sphere(name + "_capA", a, (radius,) * 3, mat)
    sphere(name + "_capB", b, (radius,) * 3, mat)
    return o

def curve_obj(name, points, bevel, mat, resolution=12):
    cu = bpy.data.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    cu.resolution_u = resolution
    cu.bevel_depth = bevel
    cu.bevel_resolution = 4
    cu.use_fill_caps = True
    sp = cu.splines.new("NURBS")
    sp.points.add(len(points) - 1)
    for p, co in zip(sp.points, points):
        p.co = (*co, 1)
    sp.use_endpoint_u = True
    sp.order_u = 4
    o = bpy.data.objects.new(name, cu)
    bpy.context.collection.objects.link(o)
    o.data.materials.append(mat)
    return o

def curl_points(style, size, rng):
    """Points of one curl in local space: grows along +Z (outward from the head)."""
    pts = []
    if style == "ringlets":      # tight corkscrew springs
        turns, r, length, n = rng.uniform(2.2, 3.2), 0.045 * size, 0.30 * size, 40
    elif style == "spirals":     # flat snail-shell spirals
        turns, r, length, n = rng.uniform(1.5, 2.2), 0.09 * size, 0.06 * size, 36
    else:                        # poofy: short fat loose coils
        turns, r, length, n = rng.uniform(1.0, 1.6), 0.07 * size, 0.16 * size, 30
    phase = rng.uniform(0, math.tau)
    for i in range(n):
        t = i / (n - 1)
        ang = phase + t * turns * math.tau
        rr = r * (1 - 0.65 * t) if style == "spirals" else r * (1 - 0.25 * t)
        pts.append((rr * math.cos(ang), rr * math.sin(ang), t * length))
    return pts

def build_mane(head_center, head_r, p, mats, rng):
    """Distribute curls over the back/sides of the head and a ring around the face."""
    style, size, vol = p["curl_style"], p["curl_size"], p["mane_volume"]
    bevel = {"ringlets": 0.032, "spirals": 0.034, "poofy": 0.05}[style] * size
    golden = math.pi * (3 - math.sqrt(5))
    n = p["curl_count"]
    made = 0
    full = p.get("pose") == "standing" or p.get("mane_core_small")   # seen from behind: cover the whole head
    cand = n * 2 if full else n * 3
    for i in range(cand):
        if made >= n and not full:
            break
        # Fibonacci sphere direction
        y = 1 - (i / (cand - 1)) * 2
        rad = math.sqrt(1 - y * y)
        th = golden * i
        d = Vector((math.cos(th) * rad, y, math.sin(th) * rad))  # here y is "front/back" axis before remap
        d = Vector((d.x, -d.y, d.z))  # -Y is the face direction
        # Skip the face area (front-facing directions) and keep a fringe around it.
        front = -d.y
        if front > 0.55 and d.z > -0.75:
            continue
        if d.z < -0.55 and front < 0.2:  # under the chin/back of neck: sparse
            if rng.random() < 0.6:
                continue
        base = head_center + d * head_r * (0.9 + 0.12 * vol)
        mat = mats[0] if rng.random() < 0.7 else mats[1]
        pts = curl_points(style, size * rng.uniform(0.85, 1.2) * (0.9 + 0.2 * vol), rng)
        o = curve_obj(f"curl_{made}", pts, bevel * rng.uniform(0.9, 1.15), mat)
        # Orient local +Z along the outward direction, random twist.
        q = Vector((0, 0, 1)).rotation_difference(d)
        o.rotation_mode = "QUATERNION"
        twist = Matrix.Rotation(rng.uniform(0, math.tau), 4, "Z").to_quaternion()
        o.rotation_quaternion = q @ twist
        o.location = base
        made += 1
    # Fluffy under-layer so no gaps show between curls.
    if p.get("pose") == "standing" or p.get("mane_core_small"):
        # Seen side-on: keep the filler inside the curls so the back of the mane stays curly.
        sphere("mane_core", head_center + Vector((0, 0.04, 0.0)), (head_r * 0.98 * vol, head_r * 0.9 * vol, head_r * 0.98 * vol), mats[0])
    else:
        sphere("mane_core", head_center + Vector((0, 0.10, 0.0)), (head_r * 1.12 * vol, head_r * 0.95 * vol, head_r * 1.12 * vol), mats[0])

def head_center(p):
    """Head centre for the pose: seated (upright body) or standing (on all fours, facing -Y)."""
    hr = 0.52 * p["head_scale"]
    if p.get("pose") == "standing":
        return Vector((0, -0.62, 0.78 + hr * 0.8))
    return Vector((0, -0.08, 0.93 + hr))

def build_standing_body(p, fur, light, dark, mane_a, rng):
    """Four-legged standing body, facing -Y. Front legs at y=-0.32, back legs at y=+0.58."""
    sphere("body", (0, 0.15, 0.66), (0.4, 0.66, 0.36), fur)
    sphere("chest", (0, -0.32, 0.72), (0.38, 0.32, 0.38), fur)
    sphere("rump", (0, 0.6, 0.68), (0.38, 0.32, 0.36), fur)
    sphere("belly", (0, 0.05, 0.44), (0.28, 0.48, 0.15), light)
    for s in (-1, 1):
        for prefix, y in (("leg", -0.32), ("bleg", 0.58)):
            x = 0.22 * s
            capsule(f"{prefix}{s}", (x, y, 0.58), (x, y, 0.14), 0.11, fur)
            paw = "paw" if prefix == "leg" else "bpaw"
            sphere(f"{paw}{s}", (x, y - 0.05, 0.08), (0.13, 0.16, 0.09), light)
            for t in (-1, 0, 1):
                sphere(f"{'toe' if prefix == 'leg' else 'btoe'}{s}{t}", (x + 0.045 * t, y - 0.2, 0.1), (0.011, 0.01, 0.028), dark, seg=8)
    tail_pts = [(0, 0.85, 0.72), (0, 1.15, 0.85), (0, 1.32, 1.05), (0, 1.3, 1.28)]
    curve_obj("tail", tail_pts, 0.045, fur)
    ts = p.get("tuft_scale", 1.0)
    tuft_c = Vector((0, 1.29, 1.3))
    for i in range(int(10 * ts * ts)):
        d = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.2, 1))).normalized()
        o = curve_obj(f"tuft{i}", curl_points(p["curl_style"], 0.45 * ts, rng), 0.016 * (2 if p["curl_style"] == "poofy" else 1), mane_a)
        o.rotation_mode = "QUATERNION"
        o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
        o.location = tuft_c

def build_lion(p):
    rng = random.Random(p["seed"])
    fur = material("fur", p["fur"], 0.6, sss=0.08)
    light = material("fur_light", p["fur_light"], 0.6, sss=0.08)
    mane_a = material("mane", p["mane"], 0.7)
    mane_b = material("mane_tip", p["mane_tip"], 0.7)
    dark = material("nose", "#3B2218", 0.3, coat=0.4)
    eye = material("eye", "#1A1210", 0.15, spec=0.6, coat=1.0)
    white = material("white", "#FFFFFF", 0.2)
    pink = material("pink", "#E8A0A0", 0.6)

    hs = p["head_scale"]
    bs = p.get("body_scale", 1.0)      # chubbiness (width/depth of body)
    es_ear = p.get("ear_scale", 1.0)
    ts = p.get("tuft_scale", 1.0)
    if p.get("pose") == "standing":
        build_standing_body(p, fur, light, dark, mane_a, rng)
    else:
        build_seated_body(p, bs, ts, fur, light, dark, mane_a, rng)
    build_head(p, hs, es_ear, fur, light, dark, mane_a, mane_b, eye, white, pink, rng)

def build_seated_body(p, bs, ts, fur, light, dark, mane_a, rng):
    # Body (seated pear) + belly
    sphere("body", (0, 0.05, 0.62), (0.55 * bs, 0.5 * bs, 0.62), fur)
    sphere("belly", (0, -0.28 * bs, 0.55), (0.36 * bs, 0.2, 0.45), light)
    # Haunches + back feet
    for s in (-1, 1):
        sphere(f"haunch{s}", (0.38 * s * bs, 0.12, 0.32), (0.3 * bs, 0.42 * bs, 0.3), fur)
        sphere(f"bfoot{s}", (0.42 * s * bs, -0.32 * bs, 0.09), (0.16, 0.24, 0.1), light)
    # Front legs + paws
    for s in (-1, 1):
        lx = 0.2 * s * (0.6 + 0.4 * bs)
        capsule(f"leg{s}", (lx, -0.25 * bs, 0.75), (lx, -0.36 * bs, 0.14), 0.12 * (0.7 + 0.3 * bs), fur)
        sphere(f"paw{s}", (lx, -0.42 * bs, 0.09), (0.15, 0.17, 0.1), light)
        for t in (-1, 0, 1):  # toe lines as tiny dark dots
            sphere(f"toe{s}{t}", (lx + 0.055 * t, -0.42 * bs - 0.16, 0.11), (0.012, 0.01, 0.03), dark, seg=8)
    # Tail with curly tuft
    tail_pts = [(0.3, 0.45, 0.25), (0.65, 0.5, 0.15), (0.85, 0.2, 0.25), (0.8, -0.05, 0.55), (0.65, -0.05, 0.75)]
    tail_pts = [(x * bs, y * bs, z) for x, y, z in tail_pts]
    curve_obj("tail", tail_pts, 0.045, fur)
    tuft_c = Vector((0.62 * bs, -0.06 * bs, 0.8))
    for i in range(int(10 * ts * ts)):
        d = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.2, 1))).normalized()
        o = curve_obj(f"tuft{i}", curl_points(p["curl_style"], 0.45 * ts, rng), 0.016 * (1 + (ts - 1) * 0.6) * (2 if p["curl_style"] == "poofy" else 1), mane_a)
        o.rotation_mode = "QUATERNION"
        o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
        o.location = tuft_c

def build_head(p, hs, es_ear, fur, light, dark, mane_a, mane_b, eye, white, pink, rng):
    # Head (bottom stays near the shoulders whatever its size)
    hr = 0.52 * hs
    hc = head_center(p)
    sphere("head", hc, (hr * 1.05, hr * 0.95, hr), fur)
    build_mane(hc, hr, p, (mane_a, mane_b), rng)
    # Ears (poke out above the mane)
    ek = hs * es_ear
    for s in (-1, 1):
        sphere(f"ear{s}", hc + Vector((0.34 * s * hs, -0.12, (0.5 * hs) + 0.03 * (es_ear - 1))), (0.14 * ek, 0.08 * ek, 0.14 * ek), fur)
        sphere(f"earin{s}", hc + Vector((0.34 * s * hs, -0.12 - 0.06 * ek, (0.5 * hs) + 0.03 * (es_ear - 1))), (0.08 * ek, 0.04 * ek, 0.09 * ek), pink)
    # Muzzle
    for s in (-1, 1):
        sphere(f"cheek{s}", hc + Vector((0.11 * s * hs, -0.42 * hs, -0.16 * hs)), (0.15 * hs, 0.12 * hs, 0.12 * hs), light)
    sphere("chin", hc + Vector((0, -0.38 * hs, -0.27 * hs)), (0.1 * hs, 0.08 * hs, 0.07 * hs), light)
    sphere("nose", hc + Vector((0, -0.54 * hs, -0.08 * hs)), (0.085 * hs, 0.06 * hs, 0.055 * hs), dark)

    def face_curve(name, pts, bevel, mat):
        return curve_obj(name, [tuple(hc + Vector(c) * hs) for c in pts], bevel * hs, mat)

    # Mouth
    mouth = p.get("mouth", "smile")
    if mouth == "cat":       # ":3" — two little bumps under the nose
        for s in (-1, 1):
            face_curve(f"mouth{s}", [(0, -0.555, -0.13), (0.015 * s, -0.56, -0.2), (0.06 * s, -0.55, -0.22), (0.1 * s, -0.52, -0.18)], 0.012, dark)
    elif mouth == "open":    # open happy mouth with tongue
        mouth_mat = material("mouth", "#5A2420", 0.5)
        tongue = material("tongue", "#E37C7C", 0.5)
        sphere("mouth_open", hc + Vector((0, -0.52 * hs, -0.22 * hs)), (0.075 * hs, 0.04 * hs, 0.055 * hs), mouth_mat)
        sphere("tongue", hc + Vector((0, -0.55 * hs, -0.25 * hs)), (0.045 * hs, 0.025 * hs, 0.025 * hs), tongue)
        face_curve("smile", [(-0.1, -0.5, -0.18), (-0.06, -0.55, -0.2), (0.06, -0.55, -0.2), (0.1, -0.5, -0.18)], 0.01, dark)
    else:
        face_curve("smile", [(-0.08, -0.52, -0.22), (-0.03, -0.55, -0.26), (0.03, -0.55, -0.26), (0.08, -0.52, -0.22)], 0.012, dark)

    if p.get("mouth_both"):
        # Hidden open mouth for animations (yawn, roar): toggled on per frame by animate_lion.py.
        cavity = material("mouth_cavity", "#4A1C1A", 0.5)
        tongue_m = material("tongue_m", "#E37C7C", 0.5)
        parts = [
            sphere("mouthopen_cavity", hc + Vector((0, -0.5 * hs, -0.24 * hs)), (0.09 * hs, 0.05 * hs, 0.085 * hs), cavity),
            sphere("mouthopen_tongue", hc + Vector((0, -0.53 * hs, -0.29 * hs)), (0.055 * hs, 0.03 * hs, 0.03 * hs), tongue_m),
        ]
        for o in parts:
            o.hide_render = True

    if p.get("blush"):
        blush = material("blush", "#F08A8A", 0.7)
        for s in (-1, 1):
            sphere(f"blush{s}", hc + Vector((0.3 * s * hs, -0.4 * hs, -0.06 * hs)), (0.07 * hs, 0.02 * hs, 0.04 * hs), blush)

    # Eyes with highlights
    es = p["eye_scale"] * hs
    eye_style = p.get("eye_style", "round")
    iris = material("iris", p.get("iris", "#7A4A1C"), 0.2, coat=1.0) if eye_style == "sparkle" else None
    for s in (-1, 1):
        ec = hc + Vector((0.2 * s * hs, -0.42 * hs, 0.1 * hs))
        if eye_style == "sparkle":   # coloured iris with a dark pupil and big glints
            sphere(f"iris{s}", ec, (0.09 * es, 0.05 * es, 0.112 * es), iris)
            sphere(f"pupil{s}", ec + Vector((0, -0.02 * es, -0.005 * es)), (0.055 * es, 0.035 * es, 0.07 * es), eye)
            sphere(f"glint{s}", ec + Vector((0.03 * s * es, -0.05 * es, 0.045 * es)), (0.032 * es,) * 3, white, seg=12)
            sphere(f"glint2{s}", ec + Vector((-0.025 * s * es, -0.052 * es, -0.035 * es)), (0.016 * es,) * 3, white, seg=8)
            sphere(f"glint3{s}", ec + Vector((0.045 * s * es, -0.05 * es, -0.01 * es)), (0.008 * es,) * 3, white, seg=8)
        else:
            sphere(f"eye{s}", ec, (0.085 * es, 0.05 * es, 0.105 * es), eye)
            sphere(f"glint{s}", ec + Vector((0.03 * s * es, -0.045 * es, 0.04 * es)), (0.026 * es,) * 3, white, seg=12)
            sphere(f"glint2{s}", ec + Vector((-0.02 * s * es, -0.05 * es, -0.03 * es)), (0.012 * es,) * 3, white, seg=8)
        # Eyebrows sit just above the eyes (move up with bigger eyes)
        brows = p.get("brows", "neutral")
        if brows != "none":
            lift = 0.105 * (p["eye_scale"] - 1)
            if brows == "raised":
                bpts = [(0.11 * s, -0.43, 0.29 + lift), (0.2 * s, -0.45, 0.34 + lift), (0.29 * s, -0.42, 0.31 + lift)]
            else:
                bpts = [(0.12 * s, -0.43, 0.26 + lift), (0.2 * s, -0.45, 0.29 + lift), (0.28 * s, -0.42, 0.26 + lift)]
            face_curve(f"brow{s}", bpts, 0.014, mane_a)

def setup_scene(p):
    sc = bpy.context.scene
    sc.render.resolution_x = sc.render.resolution_y = p["size"]
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard" if p.get("standard_view") else "AgX"
    if p["engine"] == "CYCLES":
        sc.render.engine = "CYCLES"
        sc.cycles.samples = 64
        sc.cycles.use_denoising = True
    else:
        sc.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE"
        try:
            sc.eevee.taa_render_samples = 64
            sc.eevee.use_shadows = True
            sc.eevee.use_raytracing = True
        except Exception:
            pass
    world = bpy.data.worlds.new("w")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.9, 0.9, 0.95, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    sc.world = world

    def light(name, kind, loc, energy, size=2.0, color=(1, 1, 1)):
        ld = bpy.data.lights.new(name, kind)
        ld.energy = energy
        ld.color = color
        if kind == "AREA":
            ld.size = size
        lo = bpy.data.objects.new(name, ld)
        lo.location = loc
        bpy.context.collection.objects.link(lo)
        lo.rotation_euler = (Vector((0, 0, 1.0)) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    light("key", "AREA", (-2.5, -3.5, 4.0), 450, 3.0, (1, 0.96, 0.9))
    light("fill", "AREA", (3.5, -2.5, 2.0), 200, 4.0, (0.9, 0.95, 1))
    light("rim", "AREA", (1.5, 3.5, 3.5), 450, 2.0, (1, 0.9, 0.8))

    cam_d = bpy.data.cameras.new("cam")
    cam_d.lens = 62
    cam = bpy.data.objects.new("cam", cam_d)
    bpy.context.collection.objects.link(cam)
    pos = (2.6, -5.4, 2.6) if p["camera"] == "three_quarter" else (0, -6.0, 2.2)
    cam.location = pos
    target = Vector((0, -0.05, 1.0))
    cam.rotation_euler = (target - Vector(pos)).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = dict(DEFAULTS)
    if argv:
        p.update(json.loads(argv[0]))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    build_lion(p)
    setup_scene(p)
    bpy.context.scene.render.filepath = bpy.path.abspath(p["out"]) if p["out"].startswith("//") else p["out"]
    bpy.ops.render.render(write_still=True)
    print("RENDERED", p["out"])

if __name__ == "__main__" and not globals().get("LION_IMPORT"):
    main()
