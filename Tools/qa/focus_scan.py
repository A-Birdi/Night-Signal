"""Controller focus scan (Gate 4 "controller focus"; a pad-only player needs somewhere to start on every screen).

Lists every UIScreen subclass under Assets/Game/Runtime and whether it overrides DefaultFocus. A screen without one opens
with nothing selected (ScreenRouter selects LastFocus, else DefaultFocus, else nothing), and a gamepad cannot move focus from
nothing — only Back works. Static text scan, no Unity needed; exits 1 when a screen lacks a default focus.

    py Tools/qa/focus_scan.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DECL = re.compile(r"class\s+(\w+)\s*:\s*UIScreen\b")


def main():
    missing, total = [], 0
    for root, _, files in os.walk(os.path.join(ROOT, "Assets", "Game", "Runtime")):
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            text = open(path, encoding="utf-8").read()
            found = list(DECL.finditer(text))
            for i, m in enumerate(found):
                body = text[m.start():found[i + 1].start() if i + 1 < len(found) else len(text)]
                total += 1
                if "override Selectable DefaultFocus" not in body:
                    missing.append(f"{m.group(1)}  {os.path.relpath(path, ROOT).replace(os.sep, '/')}")
    print(f"{total} screens; {len(missing)} without a default focus")
    for line in missing:
        print("  " + line)
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
