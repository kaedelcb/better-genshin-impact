"""STDIO MCP with only paginated list/read/literal-search over a frozen file map."""
import json
from pathlib import Path
import sys
from review_support import load, safe_bytes, sha, require

class Reader:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.files = load(self.root / 'files.json')

    def read(self, name):
        require(name in self.files, 'not in frozen snapshot')
        b = safe_bytes(self.root, name)
        require(sha(b) == self.files[name], 'snapshot drift')
        return b.decode('utf-8-sig')

    def call(self, args):
        op, query, start = args['operation'], args.get('query', ''), args.get('start', 0)
        require(type(start) is int and start >= 0 and isinstance(query, str), 'invalid paging/query')
        if op == 'list':
            matches = sorted(p for p in self.files if query.casefold() in p.casefold())
            return {'total': len(matches), 'files': matches[start:start + 200]}
        if op == 'read':
            lines = self.read(query).splitlines()
            return {'total_lines': len(lines), 'lines': [f'{i+1}: {s}' for i, s in
                    enumerate(lines) if start <= i < start + 160]}
        require(op == 'search' and query, 'nonempty literal search required')
        matches = []
        for p in self.files:
            for i, line in enumerate(self.read(p).splitlines()):
                if query.casefold() in line.casefold():
                    matches.append({'path': p, 'line': i + 1, 'text': line[:800]})
        return {'total': len(matches), 'matches': matches[start:start + 100]}

TOOL = {'name': 'inspect_snapshot',
        'description': 'Read-only frozen repository. list files, read numbered chunks, or search literal text across all files. Navigation is not an allowlist. start is zero-based pagination. No shell or writes.',
        'inputSchema': {'type': 'object', 'properties': {
            'operation': {'type': 'string', 'enum': ['list', 'read', 'search']},
            'query': {'type': 'string'}, 'start': {'type': 'integer', 'minimum': 0}},
            'required': ['operation'], 'additionalProperties': False},
        'annotations': {'readOnlyHint': True, 'destructiveHint': False, 'openWorldHint': False}}

def serve(root):
    reader = Reader(root)
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    for line in sys.stdin:
        request = json.loads(line)
        ident, method = request.get('id'), request.get('method')
        if ident is None:
            continue
        if method == 'initialize':
            result = {'protocolVersion': '2024-11-05', 'capabilities': {'tools': {}},
                      'serverInfo': {'name': 'review_snapshot', 'version': '3'}}
        elif method == 'tools/list':
            result = {'tools': [TOOL]}
        elif method == 'tools/call':
            try:
                require(request['params']['name'] == TOOL['name'], 'unknown tool')
                result = {'content': [{'type': 'text', 'text': json.dumps(
                    reader.call(request['params']['arguments']), ensure_ascii=False)}]}
            except (ValueError, OSError, KeyError, TypeError) as exc:
                result = {'isError': True, 'content': [{'type': 'text', 'text': str(exc)}]}
        elif method == 'ping':
            result = {}
        else:
            print(json.dumps({'jsonrpc': '2.0', 'id': ident, 'error': {'code': -32601,
                              'message': 'unsupported method'}}), flush=True)
            continue
        print(json.dumps({'jsonrpc': '2.0', 'id': ident, 'result': result}, ensure_ascii=False), flush=True)

if __name__ == '__main__':
    serve(sys.argv[1])
