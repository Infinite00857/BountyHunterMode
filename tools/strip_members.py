#!/usr/bin/env python3
"""Remove C# methods (by a signature substring) from a copied MphRead file, with brace matching.
Usage: strip_members.py <file> <signature substring> [...]   e.g. "void AddRenderItem(Scene scene)"
Used to cut MphRead's OpenGL/scene code out of the files the mod reuses."""
import sys
path = sys.argv[1]
lines = open(path).read().split('\n')
for sig in sys.argv[2:]:
    while True:
        idx = next((i for i, l in enumerate(lines) if sig in l and not l.strip().startswith('//')), None)
        if idx is None:
            break
        # include attribute/comment lines directly above
        start = idx
        while start > 0 and lines[start - 1].strip().startswith(('[', '///')):
            start -= 1
        depth, end, seen = 0, idx, False
        for j in range(idx, len(lines)):
            depth += lines[j].count('{') - lines[j].count('}')
            if '{' in lines[j]:
                seen = True
            if seen and depth <= 0:
                end = j
                break
            if not seen and lines[j].rstrip().endswith(';'):
                end = j
                break
        del lines[start:end + 1]
        print(f"{path}: removed '{sig}' ({end - start + 1} lines)")
open(path, 'w').write('\n'.join(lines))
