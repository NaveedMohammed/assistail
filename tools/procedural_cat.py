"""
Builds a cute cartoon cat (seated) from primitives and renders a still.

Run headless (PowerShell 5.1 mangles quotes in native arguments, so a .json file path also works):
  blender -b -P tools/procedural_cat.py -- '{"out": "design/cat-1/a.png", "coat": "calico"}'
  blender -b -P tools/procedural_cat.py -- options.json

Reuses the helpers in procedural_lion.py and keeps the same part names (head, ear, cheek, nose, eye, leg, paw, toe,
tail, ...) so the animation rig in animate_lion.py can be shared. Coat patterns come from shaders that read a "rest"
attribute (each vertex's position at build time), so patches and stripes stay put on the fur when parts move.

Coats: calico | tabby | solid | point (Siamese-style dark ears, face, legs and tail).
"White parts" (chest, muzzle, socks) take the colours in "belly", "muzzle" and "paws".
Designs: body_scale, head_scale, head_width, ear_scale, ear_style (pointed | fold | tufted), eye_scale, fluffy, tail_curl.
"""
import bpy, json, math, os, random, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("procedural_lion", os.path.join(HERE, "procedural_lion.py"))
lion = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(lion)   # guarded: doesn't render on import
sphere, capsule, curve_obj, material, hex_rgba = lion.sphere, lion.capsule, lion.curve_obj, lion.material, lion.hex_rgba

PALETTES = {   # calico colours: white + patch colour a + patch colour b
    "classic": {"white": "#F7F2E8", "a": "#D9822B", "b": "#2E2724"},
    "dilute":  {"white": "#F5F0E8", "a": "#E8C496", "b": "#7F8794"},
    "warm":    {"white": "#FAF1E2", "a": "#C9661E", "b": "#4A3226"},
    "classic_cream": {"white": "#EBD5B0", "a": "#D9822B", "b": "#2E2724"},   # classic with cream instead of white
    "caliby":  {"white": "#F4EFE6", "a": "#C47A3E", "b": "#3B2F27"},   # muted ginger + dark brown (reference photos)
}

DEFAULTS = {
    "out": "cat.png",
    "size": 900,
    "seed": 3,
    # Coat
    "coat": "calico",
    "palette": "classic",     # calico only
    "white_amount": 0.5,      # calico: 0 = almost all patches (tortie), 1 = mostly white
    "patch_scale": 1.5,       # calico: bigger number = smaller, busier patches
    "patch_seed": 0.0,        # calico: moves the pattern around
    "back_bias": 0.0,         # calico: >0 = coloured back, white front (like a real calico)
    "patch_stripes": 0.0,     # calico: 0..1 tabby stripes inside the colour patches ("caliby")
    "patch_soft": 0.02,       # calico: softness of the edge between the two patch colours
    "base": "#E08A3A",        # tabby / solid / point body colour
    "stripe": "#A85418",      # tabby stripes
    "stripe_scale": 2.6,
    "point": "#4A3428",       # point coat: ears, face mask, legs, tail
    "belly": None,            # white parts; None = the palette white (calico) or white
    "muzzle": None,
    "paws": None,
    "nose": "#E48C98",
    "pads": "#EFA3AC",
    # Eyes
    "iris": "#7DB24A",
    "iris2": None,            # the cat's left eye, for odd eyes
    "eye_scale": 1.25,
    # Design
    "head_scale": 1.1,
    "head_width": 1.1,
    "head_shape": "round",    # see HEAD_SHAPES
    "ear_scale": 1.0,
    "ear_style": "pointed",   # pointed | fold | tufted
    "body_scale": 0.9,
    "neck": 0.0,              # raises the head on a neck (slender, realistic look)
    "fluffy": False,          # neck ruff, bushy tail, cheek tufts
    "tail_curl": True,
    "whiskers": True,
    "paw_wrap": False,        # plaid cloth tied round the left paw (the one nearer the camera)
    "ao": 0.0,                # crease darkening strength (0 = off)
    "closeup": False,         # render a close view of the wrapped paw
    "camera": "three_quarter",
    "engine": "EEVEE",
    "standard_view": True,
}

HEAD_SHAPES = {   # w/d/h: head radii (x hr); jowl: cheek-fluff size, jx/jz: its placement; muzzle: how far it sticks out
    "round": {"w": 1.1,  "d": 0.92, "h": 0.95, "jowl": 1.0,  "jx": 1.0,  "jz": -0.16, "muzzle": 0.0,  "ear_x": 1.0,  "eye_x": 1.0},
    "wide":  {"w": 1.32, "d": 0.88, "h": 0.8,  "jowl": 1.2,  "jx": 0.95, "jz": -0.12, "muzzle": 0.0,  "ear_x": 1.25, "eye_x": 1.15, "ear_tilt": 30},
    "wedge": {"w": 0.95, "d": 0.95, "h": 0.95, "jowl": 0.5,  "jx": 0.8,  "jz": -0.1,  "muzzle": 0.07, "ear_x": 1.05, "eye_x": 0.95, "muzzle_size": 0.85},
    "jowly": {"w": 1.0,  "d": 0.92, "h": 0.98, "jowl": 1.45, "jx": 1.08, "jz": -0.26, "muzzle": 0.0,  "ear_x": 0.95, "eye_x": 1.0},
    "egg":   {"w": 0.95, "d": 0.9,  "h": 1.15, "jowl": 0.75, "jx": 0.9,  "jz": -0.22, "muzzle": 0.0,  "ear_x": 0.95, "eye_x": 0.95},
    "box":   {"w": 1.0,  "d": 0.82, "h": 0.86, "jowl": 0.9,  "jx": 1.05, "jz": -0.18, "muzzle": 0.01, "ear_x": 1.1,  "eye_x": 1.05, "box": True},
    "bean":  {"w": 1.55, "d": 0.84, "h": 0.66, "jowl": 0.55, "jx": 1.0,  "jz": -0.08, "muzzle": 0.0,  "ear_x": 1.5,  "eye_x": 1.35, "ear_tilt": 40},
}

def head_center(p):
    hr = 0.52 * p["head_scale"]
    return Vector((0, -0.08, 0.9 + hr + p.get("neck", 0.0)))

# ---------------------------------------------------------------- coat shaders

def coat_material(p, name, kind, extra_color=0.0, mask_center=None):
    """kind: calico | tabby | solid | mask (base fading to the point colour around mask_center)."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    N, L = m.node_tree.nodes, m.node_tree.links
    bsdf = N["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.62
    if "Subsurface Weight" in bsdf.inputs:
        bsdf.inputs["Subsurface Weight"].default_value = 0.06
        bsdf.inputs["Subsurface Radius"].default_value = (0.3, 0.15, 0.08)
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.3
    attr = N.new("ShaderNodeAttribute")
    attr.attribute_type = "GEOMETRY"
    attr.attribute_name = "rest"

    def ramp(fac_socket, stops):
        r = N.new("ShaderNodeValToRGB")
        els = r.color_ramp.elements
        while len(els) < len(stops):
            els.new(0.5)
        for e, (pos, col) in zip(els, stops):
            e.position = pos
            e.color = col if len(col) == 4 else hex_rgba(col)
        L.new(fac_socket, r.inputs["Fac"])
        return r

    if kind == "solid":
        bsdf.inputs["Base Color"].default_value = hex_rgba(p["base"])
        return m

    if kind == "mask":
        d = N.new("ShaderNodeVectorMath"); d.operation = "DISTANCE"
        d.inputs[1].default_value = tuple(mask_center)
        L.new(attr.outputs["Vector"], d.inputs[0])
        r = ramp(d.outputs["Value"], [(0.16, p["point"]), (0.36, p["base"])])
        L.new(r.outputs["Color"], bsdf.inputs["Base Color"])
        return m

    if kind == "tabby":
        w = N.new("ShaderNodeTexWave")
        w.wave_type = "BANDS"
        w.bands_direction = "Z"
        w.inputs["Scale"].default_value = p["stripe_scale"]
        w.inputs["Distortion"].default_value = 7.0
        w.inputs["Detail"].default_value = 1.5
        w.inputs["Detail Scale"].default_value = 1.2
        L.new(attr.outputs["Vector"], w.inputs["Vector"])
        r = ramp(w.outputs["Fac"], [(0.2, p["stripe"]), (0.27, p["base"])])
        L.new(r.outputs["Color"], bsdf.inputs["Base Color"])
        return m

    # calico
    pal = PALETTES[p["palette"]]
    def noise(scale, offset):
        add = N.new("ShaderNodeVectorMath"); add.operation = "ADD"
        add.inputs[1].default_value = offset
        L.new(attr.outputs["Vector"], add.inputs[0])
        n = N.new("ShaderNodeTexNoise")
        n.inputs["Scale"].default_value = scale
        n.inputs["Detail"].default_value = 1.5
        n.inputs["Roughness"].default_value = 0.45
        L.new(add.outputs["Vector"], n.inputs["Vector"])
        return n
    s = p["patch_seed"]
    n1 = noise(p["patch_scale"], (s * 3.1, s * 1.7, s * 2.3))          # where the colour patches are
    n2 = noise(p["patch_scale"] * 1.1, (17.3 + s, 4.1, 9.7 - s))        # colour a vs b inside a patch
    # Lower parts lean white (bellies, legs)
    sep = N.new("ShaderNodeSeparateXYZ"); L.new(attr.outputs["Vector"], sep.inputs[0])
    zb = N.new("ShaderNodeMath"); zb.operation = "MULTIPLY_ADD"
    tb = p.get("top_bias", 0.1)   # higher = the top of the cat leans coloured, legs and feet white
    zb.inputs[1].default_value = tb; zb.inputs[2].default_value = -tb
    L.new(sep.outputs["Z"], zb.inputs[0])
    mix_v = N.new("ShaderNodeMath"); mix_v.operation = "ADD"
    L.new(n1.outputs["Fac"], mix_v.inputs[0]); L.new(zb.outputs[0], mix_v.inputs[1])
    if p["back_bias"]:   # the back (+Y) leans coloured, the front (-Y) white
        yb = N.new("ShaderNodeMath"); yb.operation = "MULTIPLY"; yb.inputs[1].default_value = p["back_bias"]
        L.new(sep.outputs["Y"], yb.inputs[0])
        mv2 = N.new("ShaderNodeMath"); mv2.operation = "ADD"
        L.new(mix_v.outputs[0], mv2.inputs[0]); L.new(yb.outputs[0], mv2.inputs[1])
        mix_v = mv2
    def add_val(sock, v_sock=None, const=0.0):
        n = N.new("ShaderNodeMath"); n.operation = "ADD"
        L.new(sock, n.inputs[0])
        if v_sock is not None: L.new(v_sock, n.inputs[1])
        else: n.inputs[1].default_value = const
        return n.outputs[0]
    val = mix_v.outputs[0]
    if extra_color:   # more colour on this part (tails): push the value up instead of moving the threshold
        val = add_val(val, const=extra_color)
    if p.get("blaze"):   # white stripe down the middle of the face (front of the head only)
        ax = N.new("ShaderNodeMath"); ax.operation = "ABSOLUTE"; L.new(sep.outputs["X"], ax.inputs[0])
        bx = N.new("ShaderNodeMapRange"); bx.inputs["From Min"].default_value = 0.11; bx.inputs["From Max"].default_value = 0.05
        L.new(ax.outputs[0], bx.inputs["Value"])
        by = N.new("ShaderNodeMapRange"); by.inputs["From Min"].default_value = -0.3; by.inputs["From Max"].default_value = -0.45
        L.new(sep.outputs["Y"], by.inputs["Value"])
        bz = N.new("ShaderNodeMapRange"); bz.inputs["From Min"].default_value = 1.1; bz.inputs["From Max"].default_value = 1.25
        L.new(sep.outputs["Z"], bz.inputs["Value"])
        m1 = N.new("ShaderNodeMath"); m1.operation = "MULTIPLY"; L.new(bx.outputs[0], m1.inputs[0]); L.new(by.outputs[0], m1.inputs[1])
        m2 = N.new("ShaderNodeMath"); m2.operation = "MULTIPLY"; L.new(m1.outputs[0], m2.inputs[0]); L.new(bz.outputs[0], m2.inputs[1])
        neg = N.new("ShaderNodeMath"); neg.operation = "MULTIPLY"; neg.inputs[1].default_value = -1.0
        L.new(m2.outputs[0], neg.inputs[0])
        val = add_val(val, neg.outputs[0])
    t = 0.36 + 0.2 * p["white_amount"]
    r1 = ramp(val, [(t - 0.015, (0, 0, 0, 1)), (t + 0.015, (1, 1, 1, 1))])
    sft = p["patch_soft"]
    mid = 0.5 - p.get("b_amount", 0.0)   # b_amount > 0 = more of colour b (black/brown)
    r2 = ramp(n2.outputs["Fac"], [(mid - sft, pal["a"]), (mid + sft, pal["b"])])
    patch_col = r2.outputs["Color"]
    if p["patch_stripes"]:   # darker tabby bands inside the colour patches
        w = N.new("ShaderNodeTexWave"); w.wave_type = "BANDS"; w.bands_direction = "Z"
        w.inputs["Scale"].default_value = p["stripe_scale"] * 1.2
        w.inputs["Distortion"].default_value = 6.0
        w.inputs["Detail"].default_value = 2.0
        L.new(attr.outputs["Vector"], w.inputs["Vector"])
        sm = ramp(w.outputs["Fac"], [(0.16, (p["patch_stripes"],) * 3 + (1,)), (0.34, (0, 0, 0, 1))])
        mul = N.new("ShaderNodeMix"); mul.data_type = "RGBA"; mul.blend_type = "MULTIPLY"
        mul.inputs["B"].default_value = (0.35, 0.3, 0.27, 1)
        L.new(sm.outputs["Color"], mul.inputs["Factor"]); L.new(patch_col, mul.inputs["A"])
        patch_col = mul.outputs["Result"]
    mix = N.new("ShaderNodeMix"); mix.data_type = "RGBA"
    mix.inputs["A"].default_value = hex_rgba(pal["white"])
    L.new(r1.outputs["Color"], mix.inputs["Factor"])
    L.new(patch_col, mix.inputs["B"])
    L.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    return m

def coat_materials(p, hc, hs):
    """Material per body region for the chosen coat."""
    coat = p["coat"]
    white = PALETTES[p["palette"]]["white"] if coat == "calico" else "#F7F3EC"
    def plain(name, col):
        return material(name, col, 0.62, sss=0.06)
    belly = plain("fur_light", p["belly"] or white)
    muzzle = plain("fur_muzzle", p["muzzle"] or p["belly"] or white)
    paws = plain("fur_paws", p["paws"] or p["belly"] or white)
    M = {"belly": belly, "muzzle": muzzle, "paws": paws}
    if coat == "point":
        pt = dict(p, base=p["point"])
        dark = coat_material(pt, "fur_point", "solid")
        M.update(body=coat_material(p, "fur", "solid"), tail=dark, ear=dark, leg=dark,
                 head=coat_material(p, "fur_head", "mask", mask_center=hc + Vector((0, -0.5 * hs, -0.12 * hs))))
    else:
        kind = coat if coat in ("calico", "tabby") else "solid"
        fur = coat_material(p, "fur", kind)
        tail = coat_material(p, "fur_tail", kind, p.get("tail_color", 0.12)) if kind == "calico" else fur
        M.update(body=fur, tail=tail, ear=fur, leg=fur, head=fur)
    return M

# ---------------------------------------------------------------- body parts

def cone(name, loc, scale, mat, rot, radius2=0.08):
    bpy.ops.mesh.primitive_cone_add(vertices=20, radius1=1.0, radius2=radius2, depth=1.6, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    o.rotation_euler = rot
    o.data.materials.append(mat)
    lion.smooth(o, 2)
    return o

def build_ears(p, hc, hs, M, pink, ear_x, ear_z, tilt=22):
    ek = p["ear_scale"] * hs
    style = p["ear_style"]
    for s in (-1, 1):
        if style == "fold":   # Scottish-fold style: small ears folded forward and down
            ex, ez = 0.94 * ear_x * s, ear_z - 0.13 * hs
            cone(f"ear{s}", hc + Vector((ex, -0.14 * hs, ez)), (0.17 * ek, 0.09 * ek, 0.1 * ek), M["ear"],
                 (math.radians(68), math.radians(28) * s, 0), radius2=0.3)
            continue
        ex, ez = ear_x * s, ear_z - 0.05 * hs
        rot = (math.radians(-12), math.radians(tilt) * s, 0)
        cone(f"ear{s}", hc + Vector((ex, -0.02, ez)), (0.2 * ek, 0.1 * ek, 0.19 * ek), M["ear"], rot)
        cone(f"earin{s}", hc + Vector((ex * 0.98, -0.1 * ek - 0.02, ez - 0.02)), (0.13 * ek, 0.04 * ek, 0.14 * ek), pink, rot)
        if style == "tufted":   # lynx tips: a thin spike continuing the ear's axis from its tip
            h = 0.14 * ek
            tip = hc + Vector((ex + h * math.sin(math.radians(tilt)) * s, -0.02 + h * math.sin(math.radians(12)), ez + h * 0.92))
            cone(f"ear{s}_tuft", tip, (0.03 * ek, 0.03 * ek, 0.06 * ek), M["ear"], rot, radius2=0.0)

def build_fluff(p, hc, hs, M, rng):
    """Long-hair extras: a ruff of fluff around the neck and spiky cheek tufts."""
    bs = p["body_scale"]
    # Ruff: two rows of soft spikes around the front and sides of the neck, pointing outward and down
    for row, (z0, r, n) in enumerate(((0.9, 0.42, 15), (0.74, 0.47, 13))):
        for i in range(n):
            a = math.radians(-120 + 240 * i / (n - 1)) + rng.uniform(-0.05, 0.05)
            d = Vector((math.sin(a), -math.cos(a), -0.9)).normalized()
            loc = Vector((r * bs * math.sin(a), -r * bs * math.cos(a) * 0.9 + 0.03, z0 + rng.uniform(-0.03, 0.03)))
            mat = M["belly"] if abs(a) < math.radians(70) else M["body"]
            o = cone(f"ruff{row}_{i}", loc, (0.09, 0.09, 0.1 * rng.uniform(0.9, 1.2)), mat, (0, 0, 0), radius2=0.0)
            o.rotation_mode = "QUATERNION"
            o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
    # Cheek tufts sticking out sideways below the ears
    for s in (-1, 1):
        for k in range(3):
            d = Vector((s, 0.1 * k - 0.1, -0.35 - 0.3 * k)).normalized()
            base = hc + Vector((0.6 * s * hs * p["_w"] / 1.1, -0.04 - 0.03 * k, (-0.1 - 0.08 * k) * hs))
            o = cone(f"head_tuft{s}{k}", base, (0.05 * hs, 0.05 * hs, 0.07 * hs), M["head"], (0, 0, 0), radius2=0.0)
            o.rotation_mode = "QUATERNION"
            o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)

def build_cat(p):
    rng = random.Random(p["seed"])
    hs, bs = p["head_scale"], p["body_scale"]
    hc = head_center(p)
    M = coat_materials(p, hc, hs)
    dark = material("mouth_line", "#3A2A26", 0.35, coat=0.3)
    nose = material("nose", p["nose"], 0.4, coat=0.3)
    eye = material("eye", "#16100E", 0.15, spec=0.6, coat=1.0)
    glint = material("white", "#FFFFFF", 0.2)
    pink = material("pink", "#EFA3AC", 0.6)
    pads = material("pads", p["pads"], 0.6)
    whisk = material("whisker", "#FFFFFF", 0.4)

    # Body: seated pear, chest in the "belly" colour (+ a neck when the head is raised)
    if p["neck"] > 0:
        sphere("neck", (0, -0.1, 1.0 + p["neck"] * 0.5), (0.27 * bs, 0.25 * bs, 0.22 + p["neck"] * 0.7), M["body"])
    sphere("body", (0, 0.05, 0.6), (0.5 * bs, 0.46 * bs, 0.6), M["body"])
    sphere("belly", (0, -0.26 * bs, 0.58), (0.32 * bs, 0.2, 0.44), M["belly"])
    for s in (-1, 1):
        sphere(f"haunch{s}", (0.34 * s * bs, 0.12, 0.3), (0.27 * bs, 0.4 * bs, 0.28), M["body"])
        sphere(f"bfoot{s}", (0.38 * s * bs, -0.3 * bs, 0.08), (0.13, 0.22, 0.085), M["paws"])
    for s in (-1, 1):
        lx = 0.17 * s * (0.6 + 0.4 * bs)
        lr = 0.095 * (0.7 + 0.3 * bs)
        capsule(f"leg{s}", (lx, -0.24 * bs, 0.72), (lx, -0.34 * bs, 0.13), lr, M["leg"])
        sphere(f"paw{s}", (lx, -0.4 * bs, 0.08), (0.12, 0.15, 0.085), M["paws"])
        if p["paw_wrap"] and s == 1:   # the cat's left paw (+X) faces the 3/4 camera
            paw_wrap(s, (lx, -0.24 * bs, 0.72), (lx, -0.34 * bs, 0.13), lr, (lx, -0.4 * bs, 0.08), (0.12, 0.15, 0.085))
            continue
        for t in (-1, 0, 1):
            sphere(f"toe{s}{t}", (lx + 0.045 * t, -0.4 * bs - 0.14, 0.1), (0.01, 0.008, 0.025), pads, seg=8)

    # Long tail; the tip curls like the app icon's tail (fluffy: a bushy plume)
    tail = [(0.28, 0.42, 0.18), (0.66, 0.5, 0.1), (0.98, 0.3, 0.14), (1.1, 0.05, 0.4), (1.1, -0.02, 0.72)]
    if p.get("tail_wrap"):   # lying on the ground, curled round the front paws
        tail = [(0.28, 0.42, 0.16), (0.6, 0.38, 0.07), (0.72, 0.0, 0.065), (0.58, -0.42, 0.065), (0.3, -0.62, 0.07), (0.02, -0.66, 0.08)]
    elif p["tail_curl"]:
        tail += [(1.04, -0.04, 0.95), (0.9, -0.04, 1.02), (0.82, -0.04, 0.92), (0.88, -0.04, 0.82)]
    else:
        tail += [(1.06, -0.04, 0.95), (1.0, -0.04, 1.1)]
    t = curve_obj("tail", [(x * bs, y * bs, z) for x, y, z in tail], 0.12 if p["fluffy"] else 0.055, M["tail"], resolution=16)
    pts = t.data.splines[0].points
    for i, pt in enumerate(pts):
        pt.radius = 0.55 + 0.45 * min(1.0, i / 3) if p["fluffy"] else (1.0 if i < len(tail) - 3 else 0.85)

    # Head: shape preset + cheek fluff ("jowls"); ears, eyes and muzzle follow the head's surface
    hr = 0.52 * hs
    sh = dict(HEAD_SHAPES[p["head_shape"]])
    w = sh["w"] * p["head_width"] / 1.1
    a, b, c = hr * w, hr * sh["d"], hr * sh["h"]
    p["_w"] = w

    def surf(x, z, a=a, b=b, c=c, box=sh.get("box")):
        """Depth of the head's front surface at (x, z) from its centre."""
        k = (abs(x / a) ** 4 + abs(z / c) ** 4) if box else ((x / a) ** 2 + (z / c) ** 2)
        return b * math.sqrt(max(0.3 if box else 0.05, 1 - k))
    def surf_round(x, z):
        return surf(x, z, hr * p["head_width"], hr * 0.92, hr * 0.95, False)

    if sh.get("box"):
        bpy.ops.mesh.primitive_cube_add(location=hc)
        o = bpy.context.object
        o.name = "head"
        o.scale = (a, b, c)
        bev = o.modifiers.new("bev", "BEVEL"); bev.width = 0.7; bev.segments = 3; bev.affect = "EDGES"
        o.data.materials.append(M["head"])
        lion.smooth(o, 2)
    else:
        sphere("head", hc, (a, b, c), M["head"])
    for s in (-1, 1):
        j = sh["jowl"]
        sphere(f"head_fluff{s}", hc + Vector((0.4 * s * hs * w / 1.1 * sh["jx"], -0.05, sh["jz"] * hs)),
               (0.2 * hs * j, 0.18 * hs * j, 0.17 * hs * min(j, 1.2)), M["head"])
    ex = 0.32 * hs * sh["ear_x"]
    build_ears(p, hc, hs, M, pink, ex, c * math.sqrt(max(0.0, 1 - (ex / a) ** 2)) + (0.09 + 0.12 * max(0.0, sh["ear_x"] - 1)) * hs,
               sh.get("ear_tilt", 22))
    if p["fluffy"]:
        build_fluff(p, hc, hs, M, rng)

    # Face features move with the front of the face (and the muzzle can stick out further: wedge heads)
    fdy = -(surf(0, -0.15 * hs) - surf_round(0, -0.15 * hs)) - sh["muzzle"] * hs
    fc = hc + Vector((0, fdy, 0))
    mz = sh.get("muzzle_size", 1.0)
    for s in (-1, 1):
        sphere(f"cheek{s}", fc + Vector((0.1 * s * hs * mz, -0.42 * hs, -0.17 * hs)), (0.14 * hs * mz, 0.11 * hs, 0.11 * hs * mz), M["muzzle"])
    sphere("chin", fc + Vector((0, -0.38 * hs, -0.27 * hs)), (0.09 * hs * mz, 0.07 * hs, 0.06 * hs), M["muzzle"])
    sphere("nose", fc + Vector((0, -0.535 * hs, -0.1 * hs)), (0.06 * hs, 0.04 * hs, 0.04 * hs), nose)

    def face_curve(name, pts, bevel, mat):
        return curve_obj(name, [tuple(fc + Vector(c) * hs) for c in pts], bevel * hs, mat)

    for s in (-1, 1):
        face_curve(f"mouth{s}", [(0, -0.545, -0.13), (0.012 * s, -0.55, -0.19), (0.05 * s, -0.54, -0.21), (0.09 * s, -0.51, -0.18)], 0.011, dark)
    if p["whiskers"]:
        for s in (-1, 1):
            for k, (dz, droop) in enumerate(((0.0, 0.02), (-0.045, -0.02), (-0.09, -0.06))):
                face_curve(f"whisker{s}{k}", [(0.16 * s, -0.5, -0.15 + dz), (0.34 * s, -0.48, -0.13 + dz + droop * 0.5),
                                               (0.52 * s, -0.4, -0.13 + dz + droop)], 0.006, whisk)

    # Big sparkly eyes (iris2 = the cat's left eye, for odd eyes)
    es = p["eye_scale"] * hs
    irises = {1: material("iris", p["iris"], 0.2, coat=1.0)}
    irises[-1] = material("iris2", p["iris2"], 0.2, coat=1.0) if p["iris2"] else irises[1]
    for s in (-1, 1):
        ex_, ez_ = 0.2 * hs * sh["eye_x"], 0.08 * hs
        ey = -(surf(ex_, ez_) - surf_round(0.2 * hs, ez_)) - 0.42 * hs
        ec = hc + Vector((s * ex_, ey, ez_))
        sphere(f"iris{s}", ec, (0.09 * es, 0.05 * es, 0.11 * es), irises[s])
        sphere(f"pupil{s}", ec + Vector((0, -0.02 * es, -0.005 * es)), (0.05 * es, 0.035 * es, 0.075 * es), eye)
        sphere(f"glint{s}", ec + Vector((0.03 * s * es, -0.05 * es, 0.045 * es)), (0.03 * es,) * 3, glint, seg=12)
        sphere(f"glint2{s}", ec + Vector((-0.025 * s * es, -0.052 * es, -0.035 * es)), (0.015 * es,) * 3, glint, seg=8)

    bake_rest_positions({m.name for m in M.values()})
    if p["ao"]:
        add_ao(list(M.values()), p["ao"])

def bake_rest_positions(fur_names):
    """Store world-space rest positions on every fur mesh so the coat pattern is continuous across parts
    and sticks to the fur when the rig moves the parts."""
    import bmesh
    def is_fur(o):
        return any(m and m.name in fur_names for m in o.data.materials)
    for o in list(bpy.context.scene.objects):
        if o.type == "CURVE" and is_fur(o):
            bpy.ops.object.select_all(action="DESELECT")
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.convert(target="MESH")
    bpy.context.view_layer.update()
    for o in bpy.context.scene.objects:
        if o.type != "MESH" or not is_fur(o):
            continue
        if o.name.startswith("leg") and "_cap" not in o.name:
            # Capsule cylinders have one long face per side: add rings so the pattern doesn't smear along the leg.
            bm = bmesh.new(); bm.from_mesh(o.data)
            vertical = [e for e in bm.edges if abs(e.verts[0].co.z - e.verts[1].co.z) > 1e-4]
            bmesh.ops.subdivide_edges(bm, edges=vertical, cuts=8, use_grid_fill=True)
            bm.to_mesh(o.data); bm.free()
        mw = o.matrix_world
        a = o.data.attributes.new("rest", "FLOAT_VECTOR", "POINT")
        for i, v in enumerate(o.data.vertices):
            a.data[i].vector = mw @ v.co

# ---------------------------------------------------------------- shared extras (also used by stylized_cat.py)

TARTAN = [   # one repeat ("sett") of the kerchief plaid: (end position, colour)
    (0.28, "#2F7A3E"), (0.30, "#E8EEF0"), (0.38, "#2F7A3E"), (0.44, "#2E5FB8"), (0.48, "#3BA3B8"),
    (0.51, "#1E2E5E"), (0.55, "#3BA3B8"), (0.61, "#2E5FB8"), (0.78, "#2F7A3E"), (0.84, "#5A2A2A"),
    (0.86, "#6A3C8C"), (1.0, "#2F7A3E"),
]

def tartan_material(repeats=1.6):
    """Green plaid cloth (like a cotton kerchief): the same stripe sequence across and down, blended."""
    m = bpy.data.materials.new("cloth")
    m.use_nodes = True
    N, L = m.node_tree.nodes, m.node_tree.links
    bsdf = N["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.85
    if "Sheen Weight" in bsdf.inputs:
        bsdf.inputs["Sheen Weight"].default_value = 0.3
    tc = N.new("ShaderNodeTexCoord")
    sep = N.new("ShaderNodeSeparateXYZ"); L.new(tc.outputs["Object"], sep.inputs[0])
    def band(sock):
        mul = N.new("ShaderNodeMath"); mul.operation = "MULTIPLY"; mul.inputs[1].default_value = repeats
        L.new(sock, mul.inputs[0])
        fr = N.new("ShaderNodeMath"); fr.operation = "FRACT"; L.new(mul.outputs[0], fr.inputs[0])
        r = N.new("ShaderNodeValToRGB"); r.color_ramp.interpolation = "CONSTANT"
        els = r.color_ramp.elements
        while len(els) < len(TARTAN):
            els.new(0.5)
        start = 0.0
        for e, (end, col) in zip(els, TARTAN):
            e.position = start; e.color = hex_rgba(col); start = end
        L.new(fr.outputs[0], r.inputs["Fac"])
        return r.outputs["Color"]
    u = N.new("ShaderNodeMath"); u.operation = "ADD"
    L.new(sep.outputs["X"], u.inputs[0]); L.new(sep.outputs["Y"], u.inputs[1])
    mix = N.new("ShaderNodeMix"); mix.data_type = "RGBA"; mix.inputs["Factor"].default_value = 0.5
    L.new(band(u.outputs[0]), mix.inputs["A"]); L.new(band(sep.outputs["Z"]), mix.inputs["B"])
    # A fine weave so it reads as cloth, not plastic
    wn = N.new("ShaderNodeTexNoise"); wn.inputs["Scale"].default_value = 120; wn.inputs["Detail"].default_value = 2
    L.new(tc.outputs["Object"], wn.inputs["Vector"])
    bump = N.new("ShaderNodeBump"); bump.inputs["Strength"].default_value = 0.25
    L.new(wn.outputs["Fac"], bump.inputs["Height"]); L.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    L.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    return m

def paw_wrap(s, leg_top, leg_bot, leg_r, paw_loc, paw_scale, band_z=0.27):
    """A plaid cloth tied round the paw like a mitten: glove over the paw, a cuff up the wrist, a band with a knot and
    two loose ends on the outer front side. Names start with leg<s> so the rig moves it with that leg."""
    cloth = tartan_material()
    leg_top, leg_bot, paw_loc = Vector(leg_top), Vector(leg_bot), Vector(paw_loc)
    t = (leg_top.z - band_z) / (leg_top.z - leg_bot.z)
    band_c = leg_top.lerp(leg_bot, t)
    axis = (leg_top - leg_bot).normalized()
    # Mitten: a bit bigger than the paw and centred on it, so the paw never pokes through (even lifted, pads up)
    sphere(f"leg{s}_wrap_glove", paw_loc + Vector((0, -0.006, 0.0)), tuple(c * 1.22 for c in paw_scale), cloth, seg=32)
    capsule(f"leg{s}_wrap_cuff", tuple(band_c), tuple(paw_loc + Vector((0, 0.01, 0.05))), leg_r * 1.2, cloth)
    bpy.ops.mesh.primitive_torus_add(major_radius=leg_r * 1.22, minor_radius=0.026, location=band_c,
                                     major_segments=32, minor_segments=12)
    band = bpy.context.object; band.name = f"leg{s}_wrap_band"
    band.rotation_mode = "QUATERNION"; band.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(axis)
    band.data.materials.append(cloth); lion.smooth(band, 1)
    out = Vector((0.75 * s, -0.66, 0)).normalized()            # outer front side, facing the camera
    knot = band_c + out * leg_r * 1.38
    sphere(f"leg{s}_wrap_knot", knot, (0.056, 0.046, 0.05), cloth, seg=20)
    for k, d in enumerate((Vector((0.55 * s, -0.5, -0.65)), Vector((0.8 * s, -0.35, 0.25)))):
        d = d.normalized()
        bpy.ops.mesh.primitive_cone_add(vertices=16, radius1=0.068, radius2=0.03, depth=0.19, location=knot + d * 0.11)
        f = bpy.context.object; f.name = f"leg{s}_wrap_end{k}"
        f.scale = (1.0, 0.35, 1.0)
        f.rotation_mode = "QUATERNION"; f.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
        f.data.materials.append(cloth); lion.smooth(f, 2)

def add_ao(mats, strength=0.35, distance=0.12):
    """Darken creases (between the legs and the chest, under the chin) so light-coloured parts don't merge."""
    for m in mats:
        nt = m.node_tree
        bsdf = nt.nodes.get("Principled BSDF")
        if not bsdf:
            continue
        base = bsdf.inputs["Base Color"]
        if base.is_linked:
            src = base.links[0].from_socket
        else:
            rgb = nt.nodes.new("ShaderNodeRGB"); rgb.outputs[0].default_value = base.default_value; src = rgb.outputs[0]
        ao = nt.nodes.new("ShaderNodeAmbientOcclusion"); ao.inputs["Distance"].default_value = distance
        mr = nt.nodes.new("ShaderNodeMapRange"); mr.inputs["To Min"].default_value = 1 - strength
        nt.links.new(ao.outputs["AO"], mr.inputs["Value"])
        mul = nt.nodes.new("ShaderNodeMix"); mul.data_type = "RGBA"; mul.blend_type = "MULTIPLY"
        mul.inputs["Factor"].default_value = 1.0
        nt.links.new(src, mul.inputs["A"]); nt.links.new(mr.outputs["Result"], mul.inputs["B"])
        nt.links.new(mul.outputs["Result"], base)

OUTLINE_SKIP = ("glint", "eye", "iris", "pupil", "nose", "mouth", "whisker", "toe", "earin", "head_tuft", "tongue")

def add_outline(thickness, color="#3E2A22", only=None):
    """Toon outline ("inverted hull"): each part gets a slightly bigger shell with flipped normals and a dark
    back-face-only material, so every part shows a dark edge where it's in front of something else or the sky.
    `only`: name prefixes to outline (None = every part except small face details)."""
    from mathutils import Matrix
    m = bpy.data.materials.new("outline")
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = hex_rgba(color)
    b.inputs["Roughness"].default_value = 1.0
    b.inputs["Emission Color"].default_value = hex_rgba(color)
    b.inputs["Emission Strength"].default_value = 1.0
    m.use_backface_culling = True
    if hasattr(m, "use_backface_culling_shadow"):
        m.use_backface_culling_shadow = True
    # The shell must never cast a shadow on the cat: transparent to shadow rays
    nt = m.node_tree
    lp = nt.nodes.new("ShaderNodeLightPath")
    tr = nt.nodes.new("ShaderNodeBsdfTransparent")
    mx = nt.nodes.new("ShaderNodeMixShader")
    out = nt.nodes["Material Output"]
    nt.links.new(lp.outputs["Is Shadow Ray"], mx.inputs[0])
    nt.links.new(b.outputs["BSDF"], mx.inputs[1]); nt.links.new(tr.outputs["BSDF"], mx.inputs[2])
    nt.links.new(mx.outputs["Shader"], out.inputs["Surface"])
    for o in bpy.context.scene.objects:
        if o.type != "MESH" or o.name.startswith(OUTLINE_SKIP) or "_liner" in o.name:
            continue
        if only and not o.name.startswith(only):
            continue
        # Bake the object's scale into the mesh so the shell is equally thick on big and small parts
        o.data.transform(Matrix.Diagonal((*o.scale, 1.0)))
        o.scale = (1, 1, 1)
        o.data.materials.append(m)
        sol = o.modifiers.new("outline", "SOLIDIFY")
        sol.thickness = thickness
        sol.offset = 1.0
        sol.use_flip_normals = True
        sol.use_even_offset = True
        sol.material_offset = len(o.data.materials) - 1
        sol.use_rim = False

def closeup_camera(target):
    """Close view of the wrapped paw (front-left of the cat, from the camera side)."""
    cam = bpy.context.scene.camera
    target = Vector(target)
    cam.location = target + Vector((1.1, -1.9, 0.75))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = 62

def frame_camera():
    """Pull the camera back for tall designs (big heads) so the ears stay in frame."""
    top = max((o.matrix_world @ Vector(c)).z for o in bpy.context.scene.objects if o.type == "MESH" for c in o.bound_box)
    cam = bpy.context.scene.camera
    if top > 2.05:
        cam.data.lens *= 2.05 / top

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = dict(DEFAULTS)
    if argv:
        p.update(json.load(open(argv[0])) if argv[0].endswith(".json") else json.loads(argv[0]))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    build_cat(p)
    lion.setup_scene(p)
    frame_camera()
    if p["closeup"]:
        closeup_camera((0.17, -0.3, 0.2))
    bpy.context.scene.render.filepath = p["out"]
    bpy.ops.render.render(write_still=True)
    print("RENDERED", p["out"])

if __name__ == "__main__" and not globals().get("CAT_IMPORT"):
    main()
