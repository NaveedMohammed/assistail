"""
Rigs the stylized cat (tools/stylized_cat.py) by part name and renders one animation state as a PNG sequence
(+ <state>.json manifest like animate_lion.py).

  blender -b -P tools/animate_cat.py -- args.json
  args: {"state": "wave", "outdir": "design/cat-anim", "size": 360, "engine": "EEVEE"}

The look comes from design/chosen.json -> "cat". The cloth is tied on the cat's LEFT front paw (+X, nearer the screen).
"""
import bpy, json, math, os, sys, importlib.util
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
def _load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod
sc_mod = _load("stylized_cat")
anim_lion = None   # the lion's manifest/key helpers are copied here to keep the scripts independent

FPS = 15
STATES = {   # name: (frames, loops)
    # The same states (and ids) as the lion, so the app can drive either buddy
    "idle": (60, True), "hover": (20, False), "thinking": (60, True), "talking": (30, True), "happy": (24, False),
    "dragged": (30, True), "landing": (12, False), "sleep": (60, True), "turn": (8, False),
    "stand_up": (10, False), "walk": (16, True),
    "shake_head": (18, False), "jump": (22, False), "stretch_yawn": (40, False), "tail_chase": (36, False),
    "look_roar": (44, False),      # cat version: looks left and right, then a little meow
    "look_up": (30, True), "swat": (14, False),
    "wave": (30, False),           # waves with the cloth paw
    "untie_success": (100, False), # tugs the cloth with mouth + other paw, it comes off, paw goes in the mouth
    "untie_giveup": (86, False),   # tugs, can't get it off, sighs and gives up
    # Licks the mouse pointer: one version per direction; the app picks the one nearest the pointer (screen space)
    "lick_left": (34, False), "lick_upleft": (34, False), "lick_up": (34, False),
    "lick_upright": (34, False), "lick_right": (34, False), "lick_front": (34, False),
    # Cloth off (after a successful untie): bare-paw versions + putting it back on
    "idle_bare": (60, True), "turn_bare": (8, False), "stand_up_bare": (10, False), "walk_bare": (16, True),
    "retie": (78, False),          # reaches for the cloth, it wraps onto the paw, tugs the knot tight, happy
    "run_bare": (12, True),        # gallop away from the cloth (facing right; the app mirrors it)
    "dodge_bare": (14, False),     # cornered: crouch and leap past the cloth (the app moves him across)
    "caught_bare": (46, False),    # caught: ears down, sigh, sheepish look, holds out his bare paw
    "cloth": (2, False),           # sprites for the app's cloth window: frame 1 lying on the ground, frame 2 held up
}
BARE = {st for st in STATES if st.endswith("_bare")} | {"retie", "cloth"}
STANDING = {"stand_up", "walk", "stand_up_bare", "walk_bare", "run_bare", "dodge_bare"}
FACING_RIGHT = 90

def empty(name, loc, parent=None):
    e = bpy.data.objects.new(name, None)
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

def side_of(n, prefixes):
    for pre in prefixes:
        if n.startswith(pre):
            return -1 if n[len(pre):].startswith("-1") else 1
    return 1

def rig(p):
    bs, hr = p["body_scale"], p["head_r"]
    objs = [o for o in bpy.context.scene.objects if o.type in ("MESH", "CURVE")]
    R = {}
    R["root"] = empty("root", (0, 0, 0))
    R["body"] = empty("body_grp", (0, 0, 0.05), R["root"])
    standing = p.get("pose") == "standing"
    R["drop"] = empty("drop_grp", (0, 0, 0), R["root"])
    if standing:
        R["head"] = empty("head_grp", (0, -0.42, 0.8), R["body"])
        R["tail"] = empty("tail_grp", (0, 0.66, 0.56), R["body"])
        for s in (-1, 1):
            R[f"leg{s}"] = empty(f"leg_grp{s}", (0.15 * s, -0.28, 0.5), R["body"])
            R[f"bleg{s}"] = empty(f"bleg_grp{s}", (0.15 * s, 0.45, 0.5), R["body"])
        R["wrap"] = empty("wrap_grp", (0.15, -0.32, 0.12), R["leg1"])
    else:
        R["head"] = empty("head_grp", (0, -0.12, 1.02), R["body"])
        R["tail"] = empty("tail_grp", (0.26 * bs, 0.4 * bs, 0.16), R["body"])
        for s in (-1, 1):
            R[f"leg{s}"] = empty(f"leg_grp{s}", (0.15 * s, -0.36 * bs, 0.66), R["body"])
        R["wrap"] = empty("wrap_grp", (0.15, -0.5 * bs, 0.15), R["leg1"])
    by = {o.name: o for o in objs}
    for s in (-1, 1):
        R[f"ear{s}"] = empty(f"ear_grp{s}", tuple(by[f"ear{s}"].location), R["head"])
        R[f"eye{s}"] = empty(f"eye_grp{s}", tuple(by[f"eye{s}"].location), R["head"])
    for o in objs:
        n = o.name
        if n.startswith("leg1_wrap"):
            attach(o, R["wrap"])
        elif n.startswith("cloth_dropped"):
            attach(o, R["drop"])
        elif n.startswith(("eye", "glint")):
            attach(o, R[f"eye{side_of(n, ('glint2', 'glint', 'eye'))}"])
        elif n.startswith("ear"):
            attach(o, R[f"ear{side_of(n, ('earin', 'ear'))}"])
        elif n.startswith(("bleg", "bpaw", "btoe")):
            attach(o, R[f"bleg{side_of(n, ('bleg', 'bpaw', 'btoe'))}"])
        elif n.startswith(("leg", "paw", "toe")):
            attach(o, R[f"leg{side_of(n, ('leg', 'paw', 'toe'))}"])
        elif n.startswith(("head", "nose", "mouth", "whisker", "lick_tongue")):
            attach(o, R["head"])
        elif n.startswith("tail"):
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

def show(names_prefix, visible, frame):
    for o in bpy.context.scene.objects:
        if o.name.startswith(names_prefix):
            o.hide_render = not visible
            o.keyframe_insert("hide_render", frame=frame)

def blink_amount(f, at, dur=4):
    d = abs(f - at)
    return 1.0 if d >= dur / 2 + 1 else max(0.1, d / (dur / 2 + 1))

def animate(state, R, p):
    frames, _ = STATES[state]
    tau = math.tau
    base = {k: v.location.copy() for k, v in R.items()}
    tongue = bpy.data.objects.get("lick_tongue")
    tongue_s = tongue.scale.copy() if tongue else None
    release = None
    lifts = []
    for f in range(frames):
        t = f / frames
        def ramp(a, b):   # smooth 0→1 between frames a..b
            return 0.5 - 0.5 * math.cos(math.pi * min(1, max(0, (f - a) / max(1, b - a))))
        body_loc, body_rot, body_s = Vector(base["body"]), [0, 0, 0], [1, 1, 1]
        head_rot, tail_rot = [0, 0, 0], [0, 0, 0]
        leg_rot = {-1: [0, 0, 0], 1: [0, 0, 0]}
        ear_rot = {-1: [0, 0, 0], 1: [0, 0, 0]}
        wrap_off = Vector((0, 0, 0))
        eye = 1.0
        mouth_open, lick = False, 0.0
        wrap_on, drop_on = True, False
        breathe = math.sin(t * tau * 2)

        bleg_rot = {-1: [0, 0, 0], 1: [0, 0, 0]}
        root_loc, root_rot = Vector((0, 0, 0)), [0, 0, 0]
        pop = 0.0
        st = state[:-5] if state.endswith("_bare") else state
        if st == "idle":
            body_s = [1, 1, 1 + 0.015 * breathe]
            head_rot = [2 * math.sin(t * tau), 0, 6 * math.sin(t * tau)]
            tail_rot = [0, 0, 18 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 42)

        elif st == "walk":      # 4-legged walk facing right; diagonal pairs move together
            root_rot = [0, 0, FACING_RIGHT]
            ph = t * tau
            for s_, off in ((-1, 0.0), (1, math.pi)):
                leg_rot[s_] = [26 * math.sin(ph + off), 0, 0]
                bleg_rot[-s_] = [26 * math.sin(ph + off), 0, 0]
            body_loc.z += 0.022 * abs(math.cos(ph))
            body_rot = [1.5 * math.sin(ph * 2), 2 * math.sin(ph), 0]
            head_rot = [3 * math.sin(ph * 2 + 0.6), 0, -48 + 2 * math.sin(ph)]   # looks toward the viewer
            tail_rot = [8 * math.sin(ph * 2), 0, 14 * math.sin(ph)]
            eye = blink_amount(f, 9) if 6 <= f <= 12 else 1.0

        elif st == "run":       # gallop: front pair and back pair, body stretches and bunches
            root_rot = [0, 0, FACING_RIGHT]
            ph = t * tau
            for s_ in (-1, 1):
                leg_rot[s_] = [-42 * math.sin(ph) + 5 * s_, 0, 0]
                bleg_rot[s_] = [42 * math.sin(ph + 2.4) + 5 * s_, 0, 0]
            body_loc.z += 0.07 * max(0.0, math.sin(ph + 0.6))
            body_rot = [7 * math.sin(ph + 1.2), 0, 0]
            body_s = [1, 1 + 0.07 * math.sin(ph), 1 - 0.035 * math.sin(ph)]
            head_rot = [-5 * math.sin(ph + 0.6), 0, -36]
            ear_rot = {-1: [-28, 8, 0], 1: [-28, -8, 0]}           # flat back
            tail_rot = [-48 + 8 * math.sin(ph), 0, 6 * math.sin(ph)]  # streaming behind
            eye = 0.85

        elif st == "dodge":     # crouch, big leap (the app slides him past the cloth), land
            root_rot = [0, 0, FACING_RIGHT]
            head_rot = [0, 0, -36]
            ear_rot = {-1: [-25, 10, 0], 1: [-25, -10, 0]}
            tail_rot = [-40, 0, 0]
            if f < 3:
                u = f / 3
                root_loc.z = -0.06 * u
                body_rot = [5 * u, 0, 0]
                for s_ in (-1, 1):
                    leg_rot[s_] = [-12 * u, 0, 0]
                    bleg_rot[s_] = [28 * u, 0, 0]
            elif f < 11:
                u = (f - 3) / 8
                root_loc.z = 0.42 * 4 * u * (1 - u)
                arc = math.sin(math.pi * u)
                body_rot = [-14 * math.cos(math.pi * u), 0, 0]       # nose up on take-off, down for landing
                body_s = [1, 1 + 0.1 * arc, 1 - 0.05 * arc]
                for s_ in (-1, 1):
                    leg_rot[s_] = [-55 * arc, 0, 0]                   # front legs reach forward
                    bleg_rot[s_] = [50 * arc, 0, 0]                   # back legs kick out
                mouth_open = u < 0.6
                eye = 1.0
            else:
                u = (f - 11) / 3
                sq = math.sin(min(1.0, u) * math.pi)
                body_s = [1 + 0.07 * sq, 1, 1 - 0.1 * sq]

        elif st == "caught":    # gives in: ears down, sigh, sheepish look at you, holds out his bare paw
            droop = ramp(0, 6) * (1 - ramp(38, 46) * 0.6)
            ear_rot = {-1: [0, 36 * droop, 0], 1: [0, -36 * droop, 0]}
            look = ramp(8, 16)                                     # glances up at you
            head_rot = [16 * droop * (1 - look) - 8 * look, 0, 16 * look]
            sigh = math.sin(min(1.0, max(0.0, (f - 4) / 14)) * math.pi)
            body_loc.z -= 0.025 * sigh
            body_s = [1 + 0.02 * sigh, 1 + 0.02 * sigh, 1 - 0.04 * sigh]
            eye = 0.55 + 0.35 * look
            paw = ramp(22, 30) * (1 - ramp(40, 46))                # offers the bare paw
            leg_rot[1] = [-58 * paw, 6 * paw, 0]
            if paw > 0.3:
                head_rot = [-8 + 4 * paw, 10 * paw, 16]            # little head tilt, "go on then"
                eye = 0.9
            tail_rot = [0, 0, 10 * math.sin(t * tau * 1.5)]

        elif st == "stand_up":  # rises from a crouch onto four legs, facing right
            root_rot = [0, 0, FACING_RIGHT]
            u = math.sin(f / (frames - 1) * math.pi / 2)
            crouch = 1 - u
            root_loc.z = -0.14 * crouch
            body_rot = [-12 * crouch, 0, 0]
            for s_ in (-1, 1):
                leg_rot[s_] = [-10 * crouch, 0, 0]
                bleg_rot[s_] = [40 * crouch, 0, 0]
            tail_rot = [-20 * crouch, 0, 10 * math.sin(f / 3)]
            head_rot = [0, 0, -48]

        elif state == "retie":          # paw reaches for the cloth, it wraps on, tugs the knot tight, happy wiggle
            wrap_on = f >= 34
            drop_on = f < 34
            reach = ramp(4, 14) * (1 - ramp(24, 32))          # left paw out and down to the cloth beside him
            lift = ramp(26, 34) * (1 - ramp(62, 70))          # then up in front of him
            leg_rot[1] = [-15 * reach - 75 * lift, -48 * reach + 10 * lift, 0]
            head_rot = [22 * reach, 0, 30 * reach]
            body_rot = [-4 * reach, -8 * reach, 0]
            if 34 <= f < 62:                                   # two tugs on the knot with his teeth
                bend = ramp(34, 40) * (1 - ramp(56, 62))
                pull = max(0.0, math.sin((f - 40) / 8 * tau)) if f >= 40 else 0.0
                head_rot = [32 * bend - 16 * pull * bend, 0, 16 * bend + 8 * pull * bend]
                body_rot = [-10 * bend, 0, 0]
                mouth_open = bend > 0.5
                eye = 1 - 0.6 * pull
                ear_rot = {-1: [0, 12 * bend, 0], 1: [0, -12 * bend, 0]}
            if f >= 66:                                        # satisfied: happy eyes, little wiggle
                h = ramp(66, 70) * (1 - ramp(frames - 3, frames))
                eye = 1 - 0.85 * h
                w = math.sin((f - 66) * 1.3)
                body_s = [1 + 0.03 * h * w, 1, 1 - 0.03 * h * w]
                head_rot = [0, 8 * h * math.sin((f - 66) * 0.9), 0]
            tail_rot = [0, 0, 20 * math.sin(t * tau * 2)]
            pop = ramp(34, 37) if f >= 34 else 0.0              # the wrap pops onto the paw

        elif state == "cloth":
            wrap_on, drop_on = False, True

        elif st == "hover":             # perks up: ears up, small lean forward, happy blink
            k = math.sin(min(1, f / 8) * math.pi / 2)
            head_rot = [-6 * k, 0, 0]
            body_rot = [-3 * k, 0, 0]
            ear_rot = {-1: [0, -12 * k, 0], 1: [0, 12 * k, 0]}
            tail_rot = [0, 0, 25 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 14)
        elif st == "thinking":          # cloth paw to his chin, head tilted, looking up
            head_rot = [-10, 12 + 3 * math.sin(t * tau), 4 * math.sin(t * tau)]
            tail_rot = [0, 0, 8 * math.sin(t * tau * 4)]
            body_s = [1, 1, 1 + 0.01 * breathe]
            leg_rot[1] = [-86 - 4 * math.sin(t * tau * 2), 16, 0]
            eye = blink_amount(f, 30)
        elif st == "talking":
            head_rot = [6 * math.sin(t * tau * 2), 0, 4 * math.sin(t * tau)]
            body_loc.z += 0.015 * abs(math.sin(t * tau * 2))
            tail_rot = [0, 0, 14 * math.sin(t * tau * 2)]
            mouth_open = (f % 6) < 3
        elif st == "happy":             # little hop with squash & stretch
            if f < 4:
                sq = math.sin(f / 4 * math.pi / 2)
                body_s = [1 + 0.08 * sq, 1 + 0.08 * sq, 1 - 0.12 * sq]
            elif f < 16:
                u = (f - 4) / 12
                root_loc.z = 0.35 * 4 * u * (1 - u)
                body_s = [0.95, 0.95, 1.08] if u < 0.5 else [1, 1, 1]
                for s_ in (-1, 1):
                    leg_rot[s_] = [20 * math.sin(u * math.pi), 0, 0]
                tail_rot = [-25, 0, 0]
            else:
                u = (f - 16) / 8
                sq = math.sin(u * math.pi)
                body_s = [1 + 0.07 * sq, 1 + 0.07 * sq, 1 - 0.1 * sq]
            head_rot = [-6, 0, 0]
            eye = 0.15 if 4 <= f < 16 else 1.0       # happy closed eyes in the air
        elif st == "dragged":           # lifted by the scruff, legs dangling
            root_loc.z = 0.25
            body_rot = [0, 6 * math.sin(t * tau), 0]
            for s_ in (-1, 1):
                leg_rot[s_] = [25 + 15 * math.sin(t * tau * 2 + (0 if s_ > 0 else math.pi)), 0, 0]
            tail_rot = [20, 0, 20 * math.sin(t * tau * 2)]
            head_rot = [-8, 0, 0]
            ear_rot = {-1: [0, 10, 0], 1: [0, -10, 0]}
        elif st == "landing":           # bump down after being dropped
            u = f / (frames - 1)
            root_loc.z = max(0, 0.25 * (1 - u * 2.5))
            sq = math.sin(min(1, max(0, (u - 0.35) / 0.65)) * math.pi)
            body_s = [1 + 0.1 * sq, 1 + 0.1 * sq, 1 - 0.14 * sq]
        elif st == "sleep":
            slow = math.sin(t * tau)
            body_s = [1, 1, 1 + 0.025 * slow]
            head_rot = [14 + 2 * slow, 0, 8]
            ear_rot = {-1: [0, 14, 0], 1: [0, -14, 0]}
            tail_rot = [0, 0, 4 * slow]
            eye = 0.1
        elif st == "turn":              # turns to the right to walk away
            u = math.sin(f / (frames - 1) * math.pi / 2)
            root_rot = [0, 0, FACING_RIGHT * u]
        elif st == "shake_head":        # quick "nope" shake, decaying
            k = 1 - f / frames
            head_rot = [0, 0, 24 * math.sin(f / frames * tau * 2.5) * k]
            ear_rot = {s_: [0, 10 * s_ * math.sin(f / frames * tau * 2.5 + 0.5) * k, 0] for s_ in (-1, 1)}
            tail_rot = [0, 0, 12 * math.sin(t * tau * 2)]
        elif st == "jump":              # crouch, big jump with a full spin, land with a squash
            if f < 4:
                sq = math.sin(f / 4 * math.pi / 2)
                body_s = [1 + 0.1 * sq, 1 + 0.1 * sq, 1 - 0.15 * sq]
            elif f < 17:
                u = (f - 4) / 13
                root_loc.z = 0.55 * 4 * u * (1 - u)
                root_rot = [0, 0, 360 * (0.5 - 0.5 * math.cos(math.pi * u))]
                body_s = [0.94, 0.94, 1.1] if u < 0.4 else [1, 1, 1]
                for s_ in (-1, 1):
                    leg_rot[s_] = [-30 * math.sin(u * math.pi), 0, 0]
                tail_rot = [-35, 0, 0]
                head_rot = [-10, 0, 0]
            else:
                u = (f - 17) / 5
                sq = math.sin(min(1, u) * math.pi)
                body_s = [1 + 0.09 * sq, 1 + 0.09 * sq, 1 - 0.13 * sq]
        elif st == "stretch_yawn":      # leans forward with paws stretched out, big yawn, settles
            k = ramp(0, 12) * (1 - ramp(30, 40))
            body_rot = [-14 * k, 0, 0]
            body_loc.z -= 0.06 * k
            for s_ in (-1, 1):
                leg_rot[s_] = [-38 * k, 0, 0]
            y = ramp(12, 18) * (1 - ramp(28, 33))
            head_rot = [-22 * y, 0, 0]
            mouth_open = 13 <= f <= 30
            eye = 0.1 if 13 <= f <= 30 else 1.0
            tail_rot = [-15 * k, 0, 10 * math.sin(t * tau)]
        elif st == "tail_chase":        # spins twice after his tail, then a dizzy wobble
            spin = min(1, f / 30)
            root_rot = [0, 0, 720 * (0.5 - 0.5 * math.cos(math.pi * spin))]
            body_rot = [0, 10 * math.sin(math.pi * spin), 0]
            tail_rot = [-30, 0, 25]
            head_rot = [0, 0, -20 * math.sin(math.pi * spin)]
            if f >= 30:
                w = (f - 30) / 6
                head_rot = [0, 12 * math.sin(w * tau * 1.5) * (1 - w), 0]
                eye = 0.55
        elif st == "look_roar":         # looks left, right, then a little meow
            look = 0
            if f < 8: look = -35 * ramp(0, 8)
            elif f < 16: look = -35 + 70 * ramp(8, 16)
            elif f < 22: look = 35 * (1 - ramp(16, 22))
            r = ramp(23, 27) * (1 - ramp(38, 43))
            head_rot = [-14 * r, 0, look]
            body_s = [1, 1, 1 + 0.04 * r]
            ear_rot = {s_: [0, (-12 if f < 22 else 10 * r) * s_, 0] for s_ in (-1, 1)}
            mouth_open = 26 <= f <= 38
            eye = 0.6 if 26 <= f <= 38 else blink_amount(f, 19)
            tail_rot = [-25 * r, 0, 10 * math.sin(t * tau * 3)]
        elif st == "look_up":           # watching the butterfly above him
            head_rot = [-24, 0, 12 * math.sin(t * tau)]
            ear_rot = {-1: [0, -10, 0], 1: [0, 10, 0]}
            tail_rot = [0, 0, 22 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 20)
        elif st == "swat":              # quick swipe up with the cloth paw, in front of his face (never behind the cheek)
            k = math.sin(min(1, f / 5) * math.pi / 2) * (1 - ramp(7, 14))
            leg_rot[1] = [-118 * k, -28 * k, 0]
            head_rot = [-22, 0, 0]
            body_rot = [-4 * k, 6 * k, 0]
            tail_rot = [-20 * k, 0, 15]

        elif state == "wave":           # the cloth paw goes out to the side and up, then waves
            k = ramp(0, 6) * (1 - ramp(24, 30))
            # out to the side and slightly back, so on screen it shows beside his cheek (not in front of the chest)
            # Forward and out to the side at chest/chin height: in front of the body and below the big cheek,
            # so the paw is never hidden behind the head. Waves by swinging up and down.
            leg_rot[1] = [-52 * k, (-104 + 16 * math.sin((f - 6) / 6 * tau)) * k, 0]
            head_rot = [0, 8 * k, 14 * k]
            body_rot = [0, -5 * k, 0]
            tail_rot = [0, 0, 22 * math.sin(t * tau * 2)]
            eye = blink_amount(f, 15)

        elif state in ("untie_success", "untie_giveup"):
            success = state == "untie_success"
            end_tug = 52
            # Raise the cloth paw toward the mouth; bend down to it; other paw comes to help
            up = ramp(0, 10) * (1 - ramp(end_tug + (16 if success else 2), end_tug + (24 if success else 10)))
            leg_rot[1] = [-105 * up, 14 * up, 0]
            body_rot = [-10 * up, 0, 0]
            helper = ramp(6, 14) * (1 - ramp(end_tug, end_tug + 6))
            leg_rot[-1] = [-80 * helper, -26 * helper, 0]
            head_rot = [34 * up, 0, 14 * up]
            tugs = ramp(10, 12) * (1 - ramp(end_tug - 2, end_tug))
            if tugs:
                ph = (f - 12) / 10 * tau                       # one tug every 10 frames (4 tugs)
                pull = max(0.0, math.sin(ph))
                head_rot = [34 * up - 20 * pull * tugs, 0, 14 * up + 10 * pull * tugs]
                leg_rot[1][0] += 8 * pull * tugs                # the paw gets yanked a little
                mouth_open = True
                eye = 0.35 + 0.65 * (1 - pull)                  # squeezing his eyes with effort
                n = int((f - 12) // 10)
                # The cloth jiggles with each tug but stays over the paw (no bare patches)
                wrap_off = Vector((0, -0.25, -1)).normalized() * 0.008 * pull
                ear_rot = {-1: [0, 14, 0], 1: [0, -14, 0]}
                tail_rot = [0, 0, 25 * math.sin(f / 4)]
            if success:
                if end_tug <= f < end_tug + 4:                  # last big yank: head flicks up and to the side
                    y = ramp(end_tug, end_tug + 3)
                    head_rot = [34 - 45 * y, 0, 14 + 20 * y]
                    mouth_open = True
                    wrap_off = Vector((0, -0.25, -1)).normalized() * (0.02 + 0.18 * y)
                if f >= end_tug + 3:
                    wrap_on, drop_on = False, True
                    if release is None:
                        release = f
                if f >= end_tug + 4:                            # bare paw into the mouth, happy eyes
                    m = ramp(end_tug + 6, end_tug + 16) * (1 - ramp(frames - 10, frames - 2))
                    leg_rot[1] = [-112 * m, 22 * m, 0]
                    head_rot = [30 * m, 0, 16 * m]
                    body_rot = [-12 * m, 0, 0]
                    suck = math.sin((f - end_tug - 16) / 6 * tau) if f > end_tug + 16 else 0
                    leg_rot[1][0] += 3 * suck * m
                    head_rot[0] += 3 * suck * m
                    eye = 0.12 if m > 0.6 else 1.0
                    tail_rot = [0, 0, 18 * math.sin(t * tau * 3)]
            else:
                g = ramp(end_tug + 4, end_tug + 14) * (1 - ramp(frames - 8, frames))
                if f >= end_tug:                                # gives up: slump, droop, sigh
                    body_loc.z -= 0.035 * g
                    body_rot = [6 * g, 0, 0]
                    body_s = [1 + 0.03 * g, 1 + 0.03 * g, 1 - 0.04 * g * (1 + 0.5 * math.sin((f - end_tug) / 10 * tau))]
                    head_rot = [13 * g, 0, -6 * g]
                    ear_rot = {-1: [0, 32 * g, 0], 1: [0, -32 * g, 0]}
                    eye = 1 - 0.55 * g
                    tail_rot = [20 * g, 0, 0]

        elif state.startswith("lick_"):  # turns his head toward the pointer and laps at it three times
            # (pitch, yaw, body lean side) per screen direction; yaw + turns the face toward screen-right
            pitch, yaw, lean = {"left": (-6, -26, -6), "upleft": (-26, -20, -4), "up": (-34, 6, 0),
                                "upright": (-26, 38, 5), "right": (-6, 52, 7), "front": (-8, 14, 0)}[state[5:]]
            k = ramp(0, 5) * (1 - ramp(frames - 5, frames))
            lap = 0.0
            if 6 <= f < 30:
                u = ((f - 6) % 8) / 8               # one lap every 8 frames
                lap = u
                lick = math.sin(u * math.pi)        # out and back in
            head_rot = [pitch * k - 7 * lick, 0, yaw * k]
            body_rot = [-6 * k - 3 * lick, lean * k, 0]
            mouth_open = 4 <= f <= frames - 4
            eye = 0.45 if 6 <= f < 30 else 1.0
            ear_rot = {-1: [0, -8 * k, 0], 1: [0, 8 * k, 0]}
            tail_rot = [0, 0, 25 * math.sin(t * tau * 2)]

        fr = f + 1
        lifts.append(round(root_loc.z, 4))
        key(R["root"], fr, loc=root_loc, rot=root_rot)
        for s_ in (-1, 1):
            if f"bleg{s_}" in R:
                key(R[f"bleg{s_}"], fr, rot=bleg_rot[s_])
        if state == "retie":
            sc_ = (0.6 + 0.4 * pop) * (1.06 if 36 <= f <= 38 else 1.0) if f >= 34 else 1.0
            key(R["wrap"], fr, scale=[sc_] * 3)
        key(R["body"], fr, loc=body_loc, rot=body_rot, scale=body_s)
        key(R["head"], fr, rot=head_rot)
        key(R["tail"], fr, rot=tail_rot)
        key(R["wrap"], fr, loc=base["wrap"] + wrap_off)
        for s in (-1, 1):
            key(R[f"leg{s}"], fr, rot=leg_rot[s])
            key(R[f"ear{s}"], fr, rot=ear_rot[s])
            key(R[f"eye{s}"], fr, scale=[1, 1, eye])
        show("mouthopen_", mouth_open and lick == 0, fr)
        show("mouth-1", not mouth_open, fr); show("mouth1", not mouth_open, fr)
        if tongue:
            tongue.hide_render = lick <= 0.02
            tongue.keyframe_insert("hide_render", frame=fr)
            if mouth_open and lick > 0:
                show("mouthopen_cavity", True, fr)
            # Lap: the tongue comes out pointing slightly down, sweeps upward while out, then goes back in
            lap_u = ((f - 6) % 8) / 8 if (state.startswith("lick_") and 6 <= f < 30) else 0.0
            key(tongue, fr, scale=[tongue_s.x, tongue_s.y * max(0.05, lick * 1.25), tongue_s.z],
                rot=[15 - 60 * lap_u, 0, 0])
        show("leg1_wrap", wrap_on, fr)
        show("cloth_dropped", drop_on, fr)

    if state == "retie":
        sc = bpy.context.scene
        land = Vector((0.66, -0.3, 0.0))
        key(R["drop"], 1, loc=land, rot=[0, 0, 140], scale=[1, 1, 1])
        key(R["drop"], 13, loc=land, rot=[0, 0, 140], scale=[1, 1, 1])
        for fr in range(14, 35):                 # stuck to the paw once it touches it
            sc.frame_set(fr)
            pw = R["wrap"].matrix_world.translation.copy()
            u = min(1.0, (fr - 14) / 3)
            pos = land.lerp(pw + Vector((0, 0, -0.03)), u)
            k = 1 - 0.45 * max(0.0, (fr - 24) / 10)
            key(R["drop"], fr, loc=pos, rot=[0, 0, 140 - 60 * (fr - 14) / 20], scale=[k, k, k])
    if state == "cloth":                          # frame 1: on the ground; frame 2: held up by a corner
        for o in bpy.context.scene.objects:
            if o.type in ("MESH", "CURVE") and not o.name.startswith("cloth_dropped"):
                o.hide_render = True
                o.keyframe_insert("hide_render", frame=1)
                o.keyframe_insert("hide_render", frame=2)
        key(R["drop"], 1, loc=(0, 0, 0), rot=[0, 0, 140])
        key(R["drop"], 2, loc=(0, 0, 0.45), rot=[78, 0, 140])
    # The cloth falls from where it left the paw to the ground beside him
    if release is not None:
        sc = bpy.context.scene
        sc.frame_set(release)
        start = R["wrap"].matrix_world.translation.copy()
        land = Vector((0.66, -0.3, 0.0))   # beside him, level with his paws (on the taskbar's top edge)
        for i in range(0, 9):
            u = i / 8
            pos = start.lerp(land, u)
            pos.z = start.z * (1 - u * u) + 0.08 * math.sin(u * math.pi)
            key(R["drop"], release + i, loc=pos, rot=[0, 0, 140 * u])
        key(R["drop"], frames, loc=land, rot=[0, 0, 140])
        for fr in range(1, release):
            pass
    return lifts

def write_manifest(outdir, state, lifts, size, p):
    """Where the paws meet the ground in the image, so the app can sit him on the taskbar edge."""
    from bpy_extras.object_utils import world_to_camera_view
    sc = bpy.context.scene
    sc.frame_set(1)
    bpy.context.view_layer.update()
    cam = sc.camera
    def px(co):
        v = world_to_camera_view(sc, cam, Vector(co))
        return v.x, 1 - v.y
    if state in STANDING:   # facing right: nearest paws at y≈-0.15, body (with head) spans x≈-0.75..0.85
        cx, base_y = px((0, -0.15, 0)); lx, _ = px((-0.75, -0.15, 0)); rx, _ = px((0.85, -0.15, 0))
        _, up_y = px((0, -0.15, 0.25))
    else:                   # seated: front paws at y≈-0.39, body ±0.5
        py = -0.5 * p["body_scale"]
        cx, base_y = px((0, py, 0)); lx, _ = px((-0.5, -0.25, 0)); rx, _ = px((0.5, -0.25, 0))
        _, up_y = px((0, py, 0.25))
    frames, loop = STATES[state]
    manifest = {
        "state": state, "frames": frames, "fps": FPS, "loop": loop, "size": size,
        "baseline_y": round(base_y, 4), "center_x": round(cx, 4), "shadow_w": round(rx - lx, 4),
        "lift_per_unit": round((base_y - up_y) / 0.25, 4), "lift": lifts,
    }
    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, f"{state}.json"), "w") as fh:
        json.dump(manifest, fh)

def setup_render(outdir, state, size, engine):
    sc = bpy.context.scene
    frames, _ = STATES[state]
    sc.frame_start, sc.frame_end = 1, frames
    sc.render.fps = FPS
    sc.render.resolution_x = sc.render.resolution_y = size
    if engine == "CYCLES":
        sc.render.engine = "CYCLES"; sc.cycles.samples = 24; sc.cycles.use_denoising = True
    sc.render.filepath = os.path.join(outdir, state, "f_")
    cam = sc.camera
    cam.data.lens = 52
    target = Vector((0, -0.05, 1.25))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = json.load(open(argv[0])) if argv and argv[0].endswith(".json") else (json.loads(argv[0]) if argv else {})
    p = dict(sc_mod.DEFAULTS)
    with open(args.get("design", os.path.join(HERE, "..", "design", "chosen.json"))) as fh:
        p.update(json.load(fh)["cat"])
    p.update(args.get("overrides", {}))
    p["anim_parts"] = True
    state = args["state"]
    if state in BARE:
        p["paw_wrap"] = state == "retie"     # the retie needs the wrap (hidden until it goes on)
    if state in STANDING:
        p["pose"] = "standing"
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc_mod.build(p)
    sc_mod.lion.setup_scene(p)
    R = rig(p)
    lifts = animate(state, R, p)
    outdir = os.path.abspath(args["outdir"])
    size = args.get("size", 360)
    setup_render(outdir, state, size, args.get("engine", "EEVEE"))
    write_manifest(outdir, state, lifts, size, p)
    bpy.ops.render.render(animation=True)
    print("ANIMATED", state)

main()
