"""Summarises the test server log per installer run (a run starts at each /release.json): requests, bytes, and which
zip entries each range covered in full."""
import json, os, sys, zipfile

log, serve = sys.argv[1], sys.argv[2]
entries = {}
for n in os.listdir(serve):
    if n.endswith('.zip'):
        with zipfile.ZipFile(os.path.join(serve, n)) as z:
            entries[n] = [(i.header_offset, i.header_offset + 30 + len(i.filename) + i.compress_size, i.filename, i.compress_size) for i in z.infolist()]
runs = []
for line in open(log, encoding='utf-8'):
    r = json.loads(line)
    if r.get('path') == '/release.json':
        runs.append([])
    elif runs:
        runs[-1].append(r)
for i, run in enumerate(runs, 1):
    sent = sum(r.get('sent', 0) for r in run)
    codes = {}
    for r in run:
        codes[r['status']] = codes.get(r['status'], 0) + 1
    flags = [k for k in ('expired', 'dropped', 'client_gone') if any(r.get(k) for r in run)]
    print('run %d: %d requests %s, %d bytes sent %s' % (i, len(run), codes, sent, ' '.join(flags)))
    got = []
    for r in run:
        if 'name' not in r or r['name'] not in entries or 'start' not in r:
            continue
        end = r['start'] + r['sent']
        whole = [e for e in entries[r['name']] if e[0] >= r['start'] and e[1] <= end]
        if whole:
            got += [(r['name'][-11:], e[2], e[3]) for e in whole]
    for g in got:
        print('   entry %s %s %d' % g)
