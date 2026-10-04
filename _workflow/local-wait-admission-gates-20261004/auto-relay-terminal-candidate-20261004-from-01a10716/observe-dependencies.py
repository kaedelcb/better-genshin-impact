from pathlib import Path
import json, hashlib, re

r = Path.cwd().resolve()
d = Path(__file__).resolve().parent
rows = {}
projects = {}
for name in ('assistant', 'tests', 'probe'):
    raw = (d / (name + '-dependency-evaluation.json')).read_text(encoding='utf-8-sig')
    value = json.loads(raw[raw.index('{'):])
    pp = (d / (name + '-preprocessed.xml')).read_text(encoding='utf-8-sig')
    imports = sorted(set(x.strip() for x in pp.splitlines()
        if re.match(r'^[A-Za-z]:\\.*\.(props|targets|csproj)$', x.strip()) and Path(x.strip()).is_file()))
    refs = sorted(set(x['Identity'] for x in value['Items']['ReferencePath']))
    compiles = sorted(set(x['FullPath'] for x in value['Items']['Compile']))
    for path in imports + refs + compiles:
        p = Path(path)
        if p.is_file():
            b = p.read_bytes()
            rows[str(p)] = {'sha256': hashlib.sha256(b).hexdigest(), 'bytes': len(b),
                'kind': 'import' if path in imports else 'reference' if path in refs else 'compile'}
    projects[name] = {'properties': value['Properties'], 'imports': imports,
        'references': refs, 'compiles': compiles,
        'evaluation_sha256': hashlib.sha256((d / (name + '-dependency-evaluation.json')).read_bytes()).hexdigest(),
        'preprocessed_sha256': hashlib.sha256((d / (name + '-preprocessed.xml')).read_bytes()).hexdigest()}
for rel in ['MultiplayerHoeingAssistant/packages.lock.json',
            'MultiplayerHoeingAssistant/obj/project.assets.json',
            'Test/MultiplayerHoeingAssistant.UnitTest/obj/project.assets.json',
            'Test/R56ControlledWriterProbe/obj/project.assets.json']:
    p = r / rel
    b = p.read_bytes()
    rows[str(p)] = {'sha256': hashlib.sha256(b).hexdigest(), 'bytes': len(b), 'kind': 'restore-input'}
observation = {'kind': 'current evaluated dependency observation; not execution certification or exhaustive compiler-tool closure',
    'projects': projects, 'files': rows,
    'limitations': ['MSBuild import and assembly reference identities are observed after Rebuild, not authenticated capture-before-build.',
        'Transitive compiler/task/native runtime binaries beyond observed imports/references remain outside this record.',
        'Four original failures and all original review obligations remain open.']}
(d / 'current-dependency-observation.json').write_text(json.dumps(observation, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'projects': len(projects), 'files': len(rows), 'imports': {k: len(v['imports']) for k,v in projects.items()}}))
