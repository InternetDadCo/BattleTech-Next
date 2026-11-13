"""Test release server: mimics a GitHub release for the installer's --remote mode.

GET /release.json        GitHub-shaped release listing every file in --dir (digest given for vault and installer)
GET /dl/<name>           302 to /s/<token>/<name>, like GitHub's redirect to signed storage URLs
GET /s/<token>/<name>    the file, honouring Range; a token expires after --uses requests (then 403)
Faults: --drop NAME closes the connection halfway through the first large range of that file.
        --rate B/s throttles every body. Every request is logged as JSON lines to --log.
"""
import argparse, hashlib, json, os, re, socket, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ap = argparse.ArgumentParser()
ap.add_argument('--dir', required=True)
ap.add_argument('--port', type=int, default=18765)
ap.add_argument('--log', required=True)
ap.add_argument('--uses', type=int, default=4)
ap.add_argument('--drop', default=None)
ap.add_argument('--rate', type=float, default=0)
ap.add_argument('--tag', default='test-release')
ap.add_argument('--release', default=None, help='serve this JSON file as /release.json instead of listing --dir')
args = ap.parse_args()

lock = threading.Lock()
tokens = {}
counter = [0]
dropped = set()


def log(rec):
    with lock, open(args.log, 'a', encoding='utf-8') as f:
        f.write(json.dumps(rec) + '\n')


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


class H(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *a):
        pass

    def send_plain(self, code, body=b'', headers=None):
        self.send_response(code)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        host = 'http://127.0.0.1:%d' % args.port
        if self.path == '/release.json' and args.release:
            log({'path': self.path, 'status': 200})
            return self.send_plain(200, open(args.release, 'rb').read(), {'Content-Type': 'application/json'})
        if self.path == '/release.json':
            assets = []
            for n in sorted(os.listdir(args.dir)):
                p = os.path.join(args.dir, n)
                if not os.path.isfile(p):
                    continue
                a = {'name': n, 'size': os.path.getsize(p), 'browser_download_url': host + '/dl/' + n}
                if n in ('package.vault', 'BattleTechUCUSetup.exe'):
                    a['digest'] = 'sha256:' + sha(p)
                assets.append(a)
            body = json.dumps({'tag_name': args.tag, 'assets': assets}).encode()
            log({'path': self.path, 'status': 200})
            return self.send_plain(200, body, {'Content-Type': 'application/json'})
        m = re.match(r'^/dl/([^/]+)$', self.path)
        if m:
            with lock:
                counter[0] += 1
                tok = 't%d' % counter[0]
                tokens[tok] = args.uses
            log({'path': self.path, 'status': 302})
            return self.send_plain(302, b'', {'Location': '/s/%s/%s' % (tok, m.group(1))})
        m = re.match(r'^/s/([^/]+)/([^/]+)$', self.path)
        if not m:
            log({'path': self.path, 'status': 404})
            return self.send_plain(404)
        tok, name = m.groups()
        with lock:
            left = tokens.get(tok, 0)
            if left > 0:
                tokens[tok] = left - 1
        if left <= 0:
            log({'path': self.path, 'name': name, 'status': 403, 'expired': True})
            return self.send_plain(403)
        p = os.path.join(args.dir, name)
        if not os.path.isfile(p):
            log({'path': self.path, 'status': 404})
            return self.send_plain(404)
        size = os.path.getsize(p)
        rng = self.headers.get('Range')
        start, end, code = 0, size - 1, 200
        if rng:
            r = re.match(r'^bytes=(\d+)-(\d*)$', rng)
            start = int(r.group(1))
            end = min(int(r.group(2)) if r.group(2) else size - 1, size - 1)
            code = 206
        length = end - start + 1
        self.send_response(code)
        self.send_header('Content-Length', str(length))
        self.send_header('Accept-Ranges', 'bytes')
        if code == 206:
            self.send_header('Content-Range', 'bytes %d-%d/%d' % (start, end, size))
        self.end_headers()
        drop_at = None
        with lock:
            if args.drop == name and name not in dropped and length > (512 << 10):
                dropped.add(name)
                drop_at = length // 2
        sent = 0
        with open(p, 'rb') as f:
            f.seek(start)
            while sent < length:
                n = min(64 << 10, length - sent)
                if drop_at is not None and sent + n > drop_at:
                    n = drop_at - sent
                b = f.read(n)
                try:
                    self.wfile.write(b)
                except (ConnectionError, OSError):
                    log({'name': name, 'start': start, 'end': end, 'sent': sent, 'status': code, 'client_gone': True})
                    return
                sent += len(b)
                if drop_at is not None and sent >= drop_at:
                    self.wfile.flush()
                    self.connection.shutdown(socket.SHUT_RDWR)
                    self.connection.close()
                    log({'name': name, 'start': start, 'end': end, 'sent': sent, 'status': code, 'dropped': True})
                    return
                if args.rate:
                    time.sleep(len(b) / args.rate)
        log({'name': name, 'start': start, 'end': end, 'sent': sent, 'status': code})


ThreadingHTTPServer.daemon_threads = True
print('serving', args.dir, 'on', args.port, flush=True)
ThreadingHTTPServer(('127.0.0.1', args.port), H).serve_forever()
