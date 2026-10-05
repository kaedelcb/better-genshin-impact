"""One read-only Serena MCP query against an authenticated native snapshot.

Run with the executor-provisioned Serena virtual environment. No models are
started; the MCP and language-server lifetime is confined to the SDK session.
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys
import hashlib
import shutil

import yaml
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from native_review import verify_input, inventory, require

TOOLS = ['list_dir', 'find_file', 'read_file', 'search_for_pattern',
         'get_symbols_overview', 'find_symbol', 'find_referencing_symbols']


def seed_language_resources(runtime_dir, home):
    """Reuse verified installed binaries; keep project symbol caches isolated."""
    source = runtime_dir.parent/'serena-home/language_servers/static/CSharpLanguageServer'
    packages = list(source.glob('roslyn-language-server.*'))
    require(len(packages) == 1 and (packages[0]/'Microsoft.CodeAnalysis.LanguageServer.dll').is_file(),
            'provisioned Roslyn package missing or ambiguous')
    package = packages[0]
    target = home/'language_servers/static/CSharpLanguageServer'/package.name
    hashes = {}
    for file in package.rglob('*'):
        if not file.is_file(): continue
        require(not file.is_symlink(), 'language resource symlink forbidden')
        rel = file.relative_to(package); expected = hashlib.sha256(file.read_bytes()).hexdigest()
        dest = target/rel; dest.parent.mkdir(parents=True, exist_ok=True)
        if not dest.exists(): shutil.copyfile(file, dest)
        require(hashlib.sha256(dest.read_bytes()).hexdigest() == expected and
                hashlib.sha256(file.read_bytes()).hexdigest() == expected, 'language resource drift')
        hashes[rel.as_posix()] = expected
    return hashlib.sha256(json.dumps(hashes, sort_keys=True).encode('utf-8')).hexdigest()


async def query(request_dir, runtime_dir, dotnet_dir, name, arguments):
    require(False, 'Serena cache/process writes are not storage-contained; use plain read-only file/rg review')
    request_dir = Path(request_dir).resolve(); runtime_dir = Path(runtime_dir).resolve()
    q, snapshot = verify_input(request_dir)
    require(name in TOOLS, 'query tool is not read-only')
    project = Path(snapshot['source']); before = inventory(project)
    cache = request_dir / 'serena-cache'; cache.mkdir(exist_ok=True)
    home = cache / 'home'; home.mkdir(exist_ok=True)
    language_resources_sha256 = seed_language_resources(runtime_dir, home)
    metadata = cache / 'project'; metadata.mkdir(exist_ok=True)
    template = runtime_dir / 'Lib/site-packages/serena/resources'
    global_config = yaml.safe_load((template / 'serena_config.template.yml').read_text(encoding='utf-8'))
    global_config.update(web_dashboard=False, web_dashboard_open_on_launch=False, gui_log_window=False,
        trusted_project_path_patterns=[str(project).replace('\\', '/')],
        project_serena_folder_location=str(metadata).replace('\\', '/'),
        default_modes=['planning', 'no-memories', 'no-onboarding'], base_modes=[], tool_timeout=180)
    (home / 'serena_config.yml').write_text(yaml.safe_dump(global_config), encoding='utf-8')
    config = yaml.safe_load((template / 'project.template.yml').read_text(encoding='utf-8'))
    config.update(project_name='review-' + q['request_id'], language_servers=['csharp'], read_only=True,
        ignore_all_files_in_gitignore=False, ignored_paths=['**/User/**', '**/bin/**', '**/obj/**',
            '.git/**', '.kiro/**', '.codex/**', '_workflow/**'], fixed_tools=TOOLS,
        excluded_tools=[], included_optional_tools=[], activation_command=None,
        default_modes=['planning', 'no-memories', 'no-onboarding'])
    (metadata / 'project.yml').write_text(yaml.safe_dump(config), encoding='utf-8')
    context = cache / 'context.yml'
    context.write_text(yaml.safe_dump({'description': 'Fixed native review snapshot',
        'prompt': 'Read only this fixed project. No edits, shell, REPL, memories or project switching.',
        'single_project': True, 'fixed_tools': TOOLS, 'excluded_tools': [],
        'included_optional_tools': [], 'tool_description_overrides': {}}), encoding='utf-8')
    env = os.environ.copy()
    env.update(SERENA_HOME=str(home), SERENA_USAGE_REPORTING='false', PYTHONUTF8='1',
               DOTNET_ROOT=str(Path(dotnet_dir).resolve()), DOTNET_DbgEnableMiniDump='0', COMPlus_DbgEnableMiniDump='0')
    env['PATH'] = str(Path(dotnet_dir).resolve()) + os.pathsep + env['PATH']
    parameters = StdioServerParameters(command=str(runtime_dir / 'Scripts/serena.exe'),
        args=['start-mcp-server', '--project', str(project), '--context', str(context),
              '--mode', 'planning', '--enable-web-dashboard', 'false',
              '--enable-gui-log-window', 'false', '--open-web-dashboard', 'false', '--log-level', 'WARNING'], env=env)
    with (cache / 'stderr.log').open('a', encoding='utf-8') as err:
        async with stdio_client(parameters, errlog=err) as (read, write):
            async with ClientSession(read, write) as session:
                await session.initialize()
                actual = {t.name for t in (await session.list_tools()).tools}
                require(actual == set(TOOLS), 'Serena exposed unexpected capabilities')
                result = await session.call_tool(name, arguments)
    require(inventory(project) == before, 'Serena query changed snapshot input')
    value = result.model_dump(mode='json')
    value['_native_read'] = {'source_root': str(project), 'tool': name,
                            'request_id': q['request_id'], 'snapshot_hash': q['snapshot_hash'],
                            'helper_path': str(Path(__file__).resolve()),
                            'relative_path': arguments.get('relative_path'),
                            'language_resources_sha256': language_resources_sha256,
                            'helper_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    return value


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--request', required=True); p.add_argument('--runtime', required=True)
    p.add_argument('--dotnet', required=True); p.add_argument('--tool', choices=TOOLS, required=True)
    p.add_argument('--arguments-json', required=True)
    a = p.parse_args()
    result = asyncio.run(asyncio.wait_for(query(a.request, a.runtime, a.dotnet,
                                              a.tool, json.loads(a.arguments_json)), 240))
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 2 if result.get('isError') else 0


if __name__ == '__main__':
    sys.exit(main())
