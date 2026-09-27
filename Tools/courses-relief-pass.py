"""
Addendum 03 §5.1 relief pass for monotonic sprint courses: vary the grade along the route (steeper and gentler
stretches: compressions and sightline changes), add a few small counter-slopes where the grade is gentle, keep the start
and finish heights, tunnels/bridges/viaducts (+-60 m) and the first/last 100 m as authored, and never reduce the
vertical separation of stacked stretches below 6 m. Deterministic per course. Usage: relief.py <repo> [--write]
"""
import json, math, sys, os

root = sys.argv[1]
write = '--write' in sys.argv
COURSES = ['C01', 'C02', 'C04', 'C06', 'C07', 'C10', 'C12', 'C16', 'C17', 'C19', 'C20', 'C21', 'C22', 'C23', 'C25', 'FP02']
MAX_GRADE = 0.13

def plan(a, b):
    return math.hypot(b[0] - a[0], b[2] - a[2])

def grades(pts, ys):
    g = []
    for i in range(len(pts) - 1):
        d = plan(pts[i], pts[i + 1])
        g.append((ys[i + 1] - ys[i]) / d if d > 1e-6 else 0.0)
    return g

def window_grades(s, ys, w=20.0):
    # grade over ~20 m windows from the control polyline (linear between points)
    out = []
    j = 0
    for i in range(len(s)):
        j = max(j, i)
        while j < len(s) - 1 and s[j] - s[i] < w: j += 1
        if s[j] - s[i] >= w * 0.5: out.append((ys[j] - ys[i]) / (s[j] - s[i]))
    return out

report = []
for c in COURSES:
    p = os.path.join(root, 'Assets', 'Content', 'Courses', c, 'route.json')
    doc = json.load(open(p, encoding='utf-8'))
    cps = doc['controlPoints']
    pts = [cp['p'] for cp in cps]
    ys = [pt[1] for pt in pts]
    n = len(pts)
    s = [0.0]
    for i in range(n - 1): s.append(s[-1] + plan(pts[i], pts[i + 1]))
    L = s[-1]
    total = ys[-1] - ys[0]
    sign = 1 if total > 0 else -1
    keep = []  # (from, to) plan-distance ranges kept as authored
    # sections are in route metres (3D along the sampled road); plan distance is close enough with a 60 m margin
    for sec in doc.get('sections', []):
        keep.append((sec['fromMetres'] - 60, sec['toMetres'] + 60))
    keep.append((0, 100)); keep.append((L - 100, L + 1))

    # stacked stretches: pairs of points far apart along the route but close in plan
    stacked = []
    for i in range(n):
        for j in range(i + 1, n):
            if s[j] - s[i] > 200 and plan(pts[i], pts[j]) < 18:
                stacked.append((i, j, abs(ys[i] - ys[j])))
    for (i, j, sep) in stacked:
        keep.append((s[i] - 80, s[i] + 80)); keep.append((s[j] - 80, s[j] + 80))

    def kept(x):
        return any(a <= x <= b for (a, b) in keep)

    seed = sum(ord(ch) for ch in c)
    phase = (seed % 97) / 97.0 * 2 * math.pi
    lam = 780.0 + (seed % 5) * 60.0
    amp = 0.5
    # Counter-slope windows first (fixed per course): the gentlest stretches away from kept ranges, >= 700 m apart.
    base_g = grades(pts, ys)
    cands = []
    for i in range(n - 1):
        mid = (s[i] + s[i + 1]) * 0.5
        if mid < 300 or mid > L - 300: continue
        if any(kept(mid + d) for d in (-150, -75, 0, 75, 150)): continue
        cands.append((abs(base_g[i]), mid))
    cands.sort()
    windows = []
    for gabs, mid in cands:
        if len(windows) >= 3: break
        if all(abs(mid - w) > 700 for w in windows): windows.append(mid)
    def window_factor(mid):
        for w in windows:
            x = abs(mid - w)
            if x <= 50: return -0.25          # counter-slope: a gentle rise against the fall (or a dip on a climb)
            if x <= 110: return 0.3           # flatter approach / exit: the compression and the staging stretch
        return None
    for attempt in range(8):
        dys = [ys[i + 1] - ys[i] for i in range(n - 1)]
        f = []
        for i in range(n - 1):
            mid = (s[i] + s[i + 1]) * 0.5
            wf = window_factor(mid)
            if kept(mid): f.append(1.0)
            elif wf is not None: f.append(wf)
            else: f.append(max(0.2, 1.0 + amp * math.sin(2 * math.pi * mid / lam + phase)))
        new = [dys[i] * f[i] for i in range(n - 1)]
        # renormalise the modulated (not window, not kept) segments so the finish height is unchanged
        free = [i for i in range(n - 1) if not kept((s[i] + s[i + 1]) * 0.5) and window_factor((s[i] + s[i + 1]) * 0.5) is None]
        fixed = sum(new[i] for i in range(n - 1) if i not in free)
        want = total - fixed
        got = sum(new[i] for i in free)
        if abs(got) > 1e-6:
            k = want / got
            for i in free: new[i] *= k
        y2 = [ys[0]]
        for d in new: y2.append(y2[-1] + d)
        bumps = windows
        wg = window_grades(s, y2)
        worst = max(abs(g) for g in wg)
        ok = worst <= MAX_GRADE
        for (i, j, sep) in stacked:
            if abs(y2[i] - y2[j]) < min(sep, 6.0) - 0.01: ok = False
        if ok: break
        amp *= 0.7
    g2 = grades(pts, y2)
    counter = sum(1 for g in g2 if g * sign < -0.002)
    asc = sum(max(0.0, y2[i + 1] - y2[i]) for i in range(n - 1)); dsc = sum(max(0.0, y2[i] - y2[i + 1]) for i in range(n - 1))
    asc0 = sum(max(0.0, ys[i + 1] - ys[i]) for i in range(n - 1)); dsc0 = sum(max(0.0, ys[i] - ys[i + 1]) for i in range(n - 1))
    report.append(f"{c}: {n} pts, {L:.0f} m plan, {'descent' if sign < 0 else 'climb'} {abs(total):.0f} m; amp {amp:.2f} lambda {lam:.0f}; "
                  f"counter-slope segments {counter}, bumps {len(bumps)}; max 20 m grade {worst*100:.1f} %; ascent {asc0:.0f}->{asc:.0f} m, descent {dsc0:.0f}->{dsc:.0f} m; "
                  f"stacked pairs {len(stacked)}; end {ys[-1]:.2f}->{y2[-1]:.2f}")
    if write:
        import re
        # Text-preserving: change only each point's height and the revision, keeping the one-point-per-line layout.
        text = open(p, encoding='utf-8', newline='').read()
        for i, cp in enumerate(cps):
            pat = re.compile(r'("id":\s*"' + re.escape(cp['id']) + r'",\s*"p":\s*\[\s*[-0-9.eE]+\s*,\s*)([-0-9.eE]+)')
            m = pat.search(text)
            assert m, (c, cp['id'])
            y = round(y2[i], 2)
            ytxt = ('%.2f' % y).rstrip('0').rstrip('.')
            text = text[:m.start(2)] + ytxt + text[m.end(2):]
        m = re.search(r'"revision":\s*(\d+)', text)
        text = text[:m.start(1)] + str(int(m.group(1)) + 1) + text[m.end(1):]
        open(p, 'w', encoding='utf-8', newline='').write(text)
print('\n'.join(report))
