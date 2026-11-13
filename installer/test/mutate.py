"""Damages a hard-link copy of an install: replaces some files with junk and deletes others, never writing through a
link (each file is unlinked first, so the original install is untouched). Writes the touched paths to a list."""
import os, random, sys

clone, out = sys.argv[1], sys.argv[2]
rows = [l.rstrip('\n').split('\t') for l in open(os.path.join(clone, '.btu6-install.tsv'), encoding='utf-8')]
rows = [(r[0], int(r[1])) for r in rows if len(r) == 4]
rng = random.Random(4242)
largest = sorted(rows, key=lambda r: -r[1])[:200]
change = rng.sample(largest, 5)
rest = [r for r in rows if r not in change]
change += rng.sample(rest, 45)
rest = [r for r in rest if r not in change]
delete = rng.sample(rest, 20)
with open(out, 'w', encoding='utf-8') as f:
    for path, size in change:
        p = os.path.join(clone, path)
        os.remove(p)
        with open(p, 'wb') as o:
            o.write(os.urandom(4096))
        f.write('changed\t%s\t%d\n' % (path, size))
    for path, size in delete:
        os.remove(os.path.join(clone, path))
        f.write('deleted\t%s\t%d\n' % (path, size))
print('changed', len(change), 'deleted', len(delete), 'bytes', sum(s for _, s in change + delete))
