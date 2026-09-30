"""Counterexamples at the actual v3 state, version, provenance and gate boundaries."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import review_process as rp
import review_support as rs
import execution_evidence as ee
from snapshot_reader import Reader

class ProcessChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        subprocess.run(['git', 'init', '-q', str(self.root)], check=True)
        (self.root / 'src').mkdir(); (self.root / 'src/service.py').write_text('from storage import save\n', encoding='utf-8')
        (self.root / 'src/storage.py').write_text('def save(): pass\n', encoding='utf-8')
        subprocess.run(['git', '-C', str(self.root), 'add', '--', 'src'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                        'commit', '--only', '-qm', 'fixture', '--', 'src'], check=True)
        self.base = self.root / '_workflow/fixture'; self.base.mkdir(parents=True)
        self.m = {'batch': 'fixture', 'mode': 'code', 'schema_version': 2, 'sources': ['src/service.py'],
                  'opening_snapshot': '_workflow/fixture/opening.json', 'review_process': '_workflow/fixture/config.json'}
        self.c = {'version': 3, 'batch': 'fixture', 'model_policy': 'risk-assessed', 'default_model': 'gpt-6.1-sol', 'effort': 'medium',
                  'source_roots': ['src'], 'navigation': ['src/service.py'], 'scope_rationale': 'service plus full storage dependency',
                  'extra_files': [], 'plan': '_workflow/fixture/plan.json', 'history': [], 'prior_findings': []}
        self.write(self.c['plan'], {k: 'concrete contract ' + k for k in rp.PLAN_KEYS})
        self.write(self.m['opening_snapshot'], {'batch': 'fixture', 'review_process_required': True})
        self.write(self.m['review_process'], self.c)
        for module in (rp, ee):
            p = patch.object(module, 'verify_bundle', return_value='bundle'); p.start(); self.addCleanup(p.stop)
        rp.register(self.root, self.m, 'new')

    def write(self, rel, value):
        p = self.root / rel; p.parent.mkdir(parents=True, exist_ok=True); p.write_bytes(rs.encode(value))

    def assessment(self, stage='plan', complexity='bounded', risk='ordinary'):
        ref = '_workflow/fixture/assessment.json'
        value = dict(version=1, stage=stage, input_hashes=rp.assessment_inputs(self.root, self.m, self.c),
                     review_context=rp.assessment_context(self.root, self.m),
                     complexity=complexity, risk=risk,
                     effort='high' if complexity == 'complex' or risk in {'high', 'uncertain'} else 'medium',
                     model='gpt-6.1-sol',
                     analysis={k: 'fixture evidence for '+k for k in rp.RISK_TOPICS},
                     evidence_paths=['src/service.py'], why_this_model='matches assessed scope',
                     why_not_other_effort='alternative does not match assessed complexity')
        self.write(ref, value)
        return ref

    def test_model_selection_each_request_uses_risk_not_round(self):
        for complexity, risk, expected in [('bounded', 'ordinary', 'medium'),
                ('complex', 'ordinary', 'high'), ('bounded', 'high', 'high'),
                ('bounded', 'uncertain', 'high'), ('bounded', 'ordinary', 'medium')]:
            ref = self.assessment(complexity=complexity, risk=risk)
            self.assertEqual(rp.choose_model(self.root, self.m, self.c, 'plan', ref)['effort'], expected)

    def test_model_assessment_rejects_stale_missing_and_contradiction(self):
        ref = self.assessment(risk='high')
        value = rs.load(self.root/ref); value['effort'] = 'medium'; self.write(ref, value)
        with self.assertRaisesRegex(ValueError, 'contradicts'): rp.choose_model(self.root, self.m, self.c, 'plan', ref)
        ref = self.assessment(); value = rs.load(self.root/ref); value['analysis'].pop('state'); self.write(ref, value)
        with self.assertRaisesRegex(ValueError, 'incomplete'): rp.choose_model(self.root, self.m, self.c, 'plan', ref)
        ref = self.assessment(); (self.root/'src/storage.py').write_text('changed')
        with self.assertRaisesRegex(ValueError, 'stale'): rp.choose_model(self.root, self.m, self.c, 'plan', ref)

    def finding(self, obligation='implementation'):
        return dict(id='F1', severity='important', obligation=obligation, status='open', root_cause='lost update',
                    counterexample='two writers race', repair_steps='serialize save in storage.py', tests='race two writers',
                    paths=['src/storage.py'], closure_evidence=[])

    def review(self, stage='plan', findings=None, verdict='pass'):
        c, reg, local, _ = rp.registered(self.root, self.m)
        snap, meta = rp.capture_snapshot(self.root, self.m, c, local)
        out, intent = rp.reserve(local, reg, stage, rp.identity(self.root, self.m, c), rs.sha(rs.encode(meta)),
                                 manifest_hash=rp.manifest_digest(self.m) if stage == 'implementation' else None)
        report = dict(request_id=intent['request_id'], stage=stage, snapshot_hash=intent['snapshot_hash'],
                      verdict=verdict, unknowns=[], unknown_dispositions=[], reviewed_paths=['src/service.py', 'src/storage.py'],
                      discovered_paths=['src/storage.py'], coverage={k: 'inspected service/storage: '+k for k in rp.COVERAGE},
                      integrated_repair_plan='serialize save and exercise two writers' if findings else 'No required changes.', findings=findings or [])
        events = [{'type': 'item.completed', 'item': {'type': 'mcp_tool_call', 'server': 'review_snapshot',
                   'arguments': {'operation': 'read'}, 'result': {'content': []}}},
                  {'type': 'item.completed', 'item': {'type': 'agent_message', 'text': json.dumps(report)}},
                  {'type': 'turn.completed'}]
        (out / 'report.json').write_bytes(rs.encode(report))
        (out / 'events.jsonl').write_text('\n'.join(json.dumps(e) for e in events), encoding='utf-8')
        (out / 'stderr.txt').write_text('', encoding='utf-8'); (out / 'prompt.txt').write_text('fixture', encoding='utf-8')
        rs.publish(out / 'launch.json', {'argv': ['codex', '-m', intent['model'], '-s', 'read-only', '--ignore-user-config',
                   '--ephemeral', '--ignore-rules', '--skip-git-repo-check', '-C', str(snap.resolve()),
                   '--output-schema', str((out/'schema.json').resolve()), '-o', str((out/'report.json').resolve()),
                   '-c', 'model_reasoning_effort='+json.dumps(intent['effort'])]})
        rs.publish(out / 'exit.json', {'exit_code': 0})
        rs.publish(out / 'review-process-request.json', {'job': 'fixture', 'argv': rs.load(out / 'launch.json')['argv']})
        rs.publish(out / 'review-process-identity.json', {'job': 'fixture', 'creation_filetime': 1})
        rs.publish(out / 'review-process-result.json', {'exit_code': 0})
        rs.publish(out / 'review-tree-terminal.json', {'job': 'fixture', 'active_processes': 0})
        (out / 'review-stdout.log').write_bytes((out / 'events.jsonl').read_bytes())
        (out / 'review-stderr.log').write_bytes((out / 'stderr.txt').read_bytes())
        rs.publish(out / 'receipt.json', {'exit_code': 0, 'intent_hash': rs.sha(rs.encode(intent)), 'snapshot_id': snap.name,
                   'process_artifacts': {p.name: rs.sha(p.read_bytes()) for p in out.glob('review-*')},
                   'artifacts': {n: rs.sha((out / n).read_bytes()) for n in ['report.json', 'events.jsonl', 'stderr.txt', 'prompt.txt', 'launch.json', 'exit.json']}})
        return out, intent, report, meta

    def test_missing_plan_review_blocks_implementation(self):
        with self.assertRaisesRegex(ValueError, 'missing independent plan'):
            rp.implement(self.root, self.m)

    def test_dispatch_transport_contract_and_current_model(self):
        import process_runner
        self.write('login/auth.json', {'auth_mode': 'chatgpt', 'tokens': {'fixture': 'test only'}})
        evidence_path = '_workflow/fixture/final.trx'
        mutation_paths = ['_workflow/fixture/mutations/base.trx', '_workflow/fixture/mutations/mutant.trx',
                          '_workflow/fixture/mutations/restored.trx', '_workflow/fixture/mutations/patch.diff']
        for item in [evidence_path, *mutation_paths]: self.write(item, {'fixture': item})
        self.m['evidence'] = [{'path': evidence_path}]
        self.m['tests'] = [{'path': evidence_path, 'expect_success': True}]
        self.m['mutations'] = [{'baseline_trx': mutation_paths[0], 'mutant_trx': mutation_paths[1],
                                'restored_trx': mutation_paths[2], 'patch': mutation_paths[3]}]
        def transport(argv, **kw):
            out = kw['directory']; intent = rs.load(out/'intent.json')
            report = dict(request_id=intent['request_id'], stage=intent['stage'], snapshot_hash=intent['snapshot_hash'],
                verdict='pass', unknowns=[], unknown_dispositions=[], reviewed_paths=['src/service.py', 'src/storage.py'], discovered_paths=['src/storage.py'],
                coverage={k: 'transport fixture '+k for k in rp.COVERAGE}, integrated_repair_plan='test fixture only', findings=[])
            self.assertEqual(argv[argv.index('-m')+1], intent['model'])
            self.assertEqual(argv[argv.index('-s')+1], 'read-only')
            self.assertEqual(rs.load(Path(argv[argv.index('--output-schema')+1]))['properties']['stage']['enum'], ['plan'])
            rs.publish(Path(argv[argv.index('-o')+1]), report)
            events = [dict(type='item.completed', item=dict(type='mcp_tool_call', server='review_snapshot',
                      arguments={'operation': 'read'}, result={'content': []})),
                      dict(type='item.completed', item=dict(type='agent_message', text=json.dumps(report))), dict(type='turn.completed')]
            stdout, stderr = out/'review-stdout.log', out/'review-stderr.log'
            stdout.write_text('\n'.join(json.dumps(e) for e in events), encoding='utf-8'); stderr.write_text('')
            rs.publish(out/'review-process-request.json', dict(argv=argv, job='fixture'))
            rs.publish(out/'review-process-identity.json', dict(job='fixture', creation_filetime=1))
            rs.publish(out/'review-process-result.json', dict(exit_code=0))
            rs.publish(out/'review-tree-terminal.json', dict(job='fixture', active_processes=0))
            return 0, stdout, stderr
        for risk, expected in [('ordinary', 'medium'), ('high', 'high')]:
            with patch.object(process_runner, 'run', side_effect=transport):
                result = rp.dispatch(self.root, self.m, 'plan', sys.executable, self.root/'login',
                                     assessment_path=self.assessment(risk=risk))
            out = self.root/result['request']
            self.assertEqual(rs.load(out/'intent.json')['effort'], expected)
            receipt = rs.load(out/'receipt.json')
            snapshot = rp.state_dir(self.root, self.m) / 'snapshots' / receipt['snapshot_id']
            files = rs.load(snapshot/'files.json')
            self.assertTrue(set([evidence_path, *mutation_paths]) <= files.keys())
            self.assertEqual(rp.implement(self.root, self.m)['kind'], 'implementation')

    def test_approved_repair_plan_keeps_important_code_issue_open(self):
        self.review(findings=[self.finding()])
        self.assertEqual(rp.implement(self.root, self.m)['kind'], 'repair-only')
        with patch.object(ee, 'validate_manifest'):
            with self.assertRaisesRegex(ValueError, 'missing independent implementation'):
                rp.gate(self.root, self.m, 'closeout')

    def test_plan_pass_with_plan_important_is_rejected(self):
        _, intent, report, meta = self.review(findings=[self.finding('plan')])
        with self.assertRaisesRegex(ValueError, 'open important'):
            rp.validate_report(report, intent, {}, meta['files'])

    def test_complete_gate_and_new_dependency_invalidates(self):
        self.review(); rp.implement(self.root, self.m)
        (self.root / 'src/service.py').write_text('from storage import save\nsave()\n')
        rp.check_permit(self.root, self.m)  # Implementation is allowed to change approved source.
        self.review('implementation')
        with patch.object(ee, 'validate_manifest'):
            rp.gate(self.root, self.m, 'closeout')
            (self.root / 'src/new.py').write_text('new dependency')
            with self.assertRaisesRegex(ValueError, 'source/evidence drift'):
                rp.gate(self.root, self.m, 'closeout')

    def test_plan_change_invalidates_permit(self):
        self.review(); rp.implement(self.root, self.m)
        self.write(self.c['plan'], {k: 'changed contract '+k for k in rp.PLAN_KEYS})
        with self.assertRaisesRegex(ValueError, 'identity drift'):
            rp.check_permit(self.root, self.m)

    def test_external_contract_change_invalidates_permit(self):
        self.write('contract.json', {'contract': 'old'})
        self.c['extra_files'] = ['contract.json']; self.write(self.m['review_process'], self.c)
        self.review(); rp.implement(self.root, self.m)
        self.write('contract.json', {'contract': 'changed'})
        with self.assertRaisesRegex(ValueError, 'identity drift'):
            rp.check_permit(self.root, self.m)

    def test_snapshot_does_not_mistake_own_output_for_external_drift(self):
        subprocess.run(['git', '-C', str(self.root), 'add', '--', '_workflow/fixture'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                        'commit', '--only', '-qm', 'tracked workflow', '--', '_workflow/fixture'], check=True)
        c, _, local, _ = rp.registered(self.root, self.m)
        rp.capture_snapshot(self.root, self.m, c, local)

    def test_git_identity_preserves_scoped_diff_under_windows_argument_budget(self):
        docs = self.root / 'docs'; docs.mkdir()
        refs = []
        for i in range(36):
            rel = f'docs/record-{i:03d}-long-evidence-reference.json'
            (self.root / rel).write_text(f'baseline {i}\n', encoding='utf-8')
            refs.append(rel)
        for rel, text in [('staged-delete/one.txt', 'staged delete\n'), ('unstaged-delete/two.txt', 'unstaged delete\n')]:
            path = self.root / rel; path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding='utf-8')
        subprocess.run(['git', '-C', str(self.root), 'config', 'diff.renames', 'true'], check=True)
        subprocess.run(['git', '-C', str(self.root), 'add', '--', 'docs', 'staged-delete', 'unstaged-delete'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                        'commit', '--only', '-qm', 'scoped diff fixture', '--', 'docs', 'staged-delete', 'unstaged-delete'], check=True)

        old_path = self.root / refs[6]
        renamed_rel = 'docs/renamed-reference.json'
        old_path.rename(self.root / renamed_rel)
        subprocess.run(['git', '-C', str(self.root), 'add', '-A'], check=True)
        staged_rel = refs[7]
        (self.root / staged_rel).write_text('staged update\n', encoding='utf-8')
        subprocess.run(['git', '-C', str(self.root), 'add', '--', staged_rel], check=True)
        (self.root / refs[8]).write_text('unstaged update\n', encoding='utf-8')
        (self.root / refs[9]).unlink()
        staged_delete = self.root / 'staged-delete'
        (staged_delete / 'one.txt').unlink(); staged_delete.rmdir()
        subprocess.run(['git', '-C', str(self.root), 'add', '-A', '--', 'staged-delete'], check=True)
        unstaged_delete = self.root / 'unstaged-delete'
        (unstaged_delete / 'two.txt').unlink(); unstaged_delete.rmdir()

        scopes = ['docs', 'staged-delete', 'unstaged-delete', *refs, renamed_rel]
        raw_git = rs.git
        expected_unstaged = raw_git(self.root, 'diff', '--no-ext-diff', '--no-textconv', '--', *scopes).decode('utf-8')
        expected_staged = raw_git(self.root, 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--', *scopes).decode('utf-8')
        expected_source_only = raw_git(self.root, 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--', refs[6]).decode('utf-8')
        expected_destination_only = raw_git(self.root, 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--', renamed_rel).decode('utf-8')
        expected_staged_delete = raw_git(self.root, 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--', 'staged-delete').decode('utf-8')
        expected_unstaged_delete = raw_git(self.root, 'diff', '--no-ext-diff', '--no-textconv', '--', 'unstaged-delete').decode('utf-8')
        expected_dot = raw_git(self.root, 'diff', '--no-ext-diff', '--no-textconv', '--', '.').decode('utf-8')
        limit = 150
        seen_argument_sizes = []
        def bounded_git(root, *args):
            if args and args[0] == 'diff' and '--' in args:
                pathspecs = args[args.index('--') + 1:]
                size = sum(len(str(item)) + 1 for item in pathspecs)
                seen_argument_sizes.append(size)
                if size > limit:
                    raise OSError(206, 'simulated Windows CreateProcess argument limit')
            return raw_git(root, *args)

        with patch.object(rs, 'GIT_PATHSPEC_ARG_LIMIT', limit, create=True), patch.object(rs, 'git', side_effect=bounded_git):
            actual = rs.git_identity(self.root, scopes)

        self.assertTrue(seen_argument_sizes)
        self.assertLessEqual(max(seen_argument_sizes), limit)
        self.assertEqual(actual['unstaged'], expected_unstaged)
        self.assertEqual(actual['staged'], expected_staged)
        self.assertIn('rename from docs/record-006-long-evidence-reference.json', actual['staged'])
        source_only = rs.git_identity(self.root, [refs[6]])
        destination_only = rs.git_identity(self.root, [renamed_rel])
        self.assertEqual(source_only['staged'], expected_source_only)
        self.assertEqual(destination_only['staged'], expected_destination_only)
        self.assertIn('deleted file mode', source_only['staged'])
        self.assertIn('new file mode', destination_only['staged'])
        self.assertEqual(rs.git_identity(self.root, ['staged-delete'])['staged'], expected_staged_delete)
        self.assertEqual(rs.git_identity(self.root, ['unstaged-delete'])['unstaged'], expected_unstaged_delete)
        self.assertEqual(rs.git_identity(self.root, ['.'])['unstaged'], expected_dot)
        self.assertEqual(actual['unstaged'].count('diff --git '), expected_unstaged.count('diff --git '))
        self.assertEqual(actual['staged'].count('diff --git '), expected_staged.count('diff --git '))

    def test_snapshot_git_diff_covers_navigation_and_contract_refs(self):
        docs = self.root / 'docs'; docs.mkdir()
        (docs / 'contract.md').write_text('before\n', encoding='utf-8')
        subprocess.run(['git', '-C', str(self.root), 'add', '--', 'docs/contract.md'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                        'commit', '--only', '-qm', 'contract', '--', 'docs/contract.md'], check=True)
        (docs / 'contract.md').write_text('after\n', encoding='utf-8')
        self.c['navigation'].append('docs/contract.md')
        self.c['extra_files'].append('docs/contract.md')
        self.write(self.m['review_process'], self.c)
        _, _, local, _ = rp.registered(self.root, self.m)
        snap, meta = rp.capture_snapshot(self.root, self.m, self.c, local)
        self.assertIn('docs/contract.md', meta['git']['unstaged'])
        self.assertIn('docs/contract.md', meta['files'])
        self.assertEqual(rp.verify_snapshot(self.root, snap, self.m, self.c, current=True), meta)

    def test_full_workspace_status_is_distinct_from_declared_scope_diff(self):
        outside = self.root / 'outside.md'; outside.write_text('outside before\n', encoding='utf-8')
        subprocess.run(['git', '-C', str(self.root), 'add', '--', 'outside.md'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                        'commit', '--only', '-qm', 'outside baseline', '--', 'outside.md'], check=True)
        outside.write_text('outside changed\n', encoding='utf-8')
        (self.root / 'src/service.py').write_text('changed in declared scope\n', encoding='utf-8')
        git_evidence = rs.git_identity(self.root, rp.review_git_scopes(['src'], []))
        self.assertIn('outside.md', git_evidence['status'])
        self.assertNotIn('outside changed', git_evidence['unstaged'])
        self.assertIn('src/service.py', git_evidence['unstaged'])
        state_path = '_workflow/fixture/review-process/requests/001/report.json'
        self.assertNotIn(state_path, rp.review_git_scopes(['src'], ['outside.md', state_path]))

    def test_reconciled_request_and_historical_snapshot_are_frozen_for_next_review(self):
        out, _, _, _ = self.failed_discovery_attempt()
        rp.reconcile_report(self.root, self.m, '001')
        refs, historical = rp.reconciliation_evidence_sources(self.root, self.m)
        c, _, local, _ = rp.registered(self.root, self.m)
        old_snapshot = next((local / 'snapshots').glob('*'))
        snap, meta = rp.capture_snapshot(self.root, self.m, c, local)
        self.assertIn((out / 'events.jsonl').relative_to(self.root).as_posix(), refs)
        self.assertIn((out / 'review-tree-terminal.json').relative_to(self.root).as_posix(), meta['files'])
        self.assertIn((old_snapshot / 'snapshot.json').relative_to(self.root).as_posix(), historical)
        self.assertIn((old_snapshot / '__review__/git.json').relative_to(self.root).as_posix(), meta['files'])
        self.assertEqual(meta['historical_snapshot_refs'], historical)
        self.assertEqual(rp.verify_snapshot(self.root, snap, self.m, c, current=True), meta)

    def test_manifest_changes_cannot_borrow_same_batch_implementation_pass(self):
        self.review(); rp.implement(self.root, self.m); self.review('implementation')
        for key in ['tests', 'mutations', 'risk_matrix', 'evidence', 'comparison']:
            changed = dict(self.m); changed[key] = 'changed same-batch contract'
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'manifest identity drift'):
                rp.latest(self.root, changed, 'implementation', True)

    def test_full_lifecycle_real_gates_and_execution(self):
        import workflow as w
        from test_workflow import trx
        self.m.update(batch='lifecycle', opening_snapshot='_workflow/lifecycle/opening.json',
                      review_process='_workflow/lifecycle/config.json', risk_matrix='_workflow/lifecycle/matrix.json')
        self.c.update(batch='lifecycle', plan='_workflow/lifecycle/plan.json')
        self.write(self.c['plan'], {k: 'concrete lifecycle contract '+k for k in rp.PLAN_KEYS})
        self.write(self.m['review_process'], self.c)
        rows = [dict(id=k, dimension=k, scenario='bounded fixture '+k, expected='arithmetic assertion passes',
                     critical=False, status='planned') for k in ['state', 'concurrency', 'fault']]
        self.write(self.m['risk_matrix'], dict(schema_version=1, batch='lifecycle', rows=rows))
        data = trx([('t', 'target', 'Passed')])
        (self.root/'src/run.py').write_text('import pathlib,sys\nassert 1+1==2\npathlib.Path(sys.argv[1]).write_bytes('+repr(data)+')\n')
        self.write('manifest.json', self.m)
        w.begin(self.root, 'manifest.json'); rp.register(self.root, self.m, 'new')
        self.review(); rp.implement(self.root, self.m)
        recipe = dict(purpose='current_regression', conditions='bounded arithmetic fixture', input_roots=['src'],
                      input_files=[], build_argv=[], run_argv=[sys.executable, '-B', '{root}/src/run.py', '{out}/result.trx'],
                      products=[], results=['result.trx'])
        self.write('recipe.json', recipe)
        execution = ee.capture(self.root, 'recipe.json', '_workflow/lifecycle/executions')
        result = (Path(execution).parent/'result.trx').as_posix()
        for row in rows: row.update(status='covered', counterexample_ids=['test'], test_ids=['t'])
        self.write(self.m['risk_matrix'], dict(schema_version=1, batch='lifecycle', rows=rows))
        self.m.update(execution_evidence=[execution], tests=[dict(path=result, expect_success=True)], mutations=[],
            mutation_scope='arithmetic fixture; actual gate mutations tested separately',
            evidence=[dict(id='test', path=result, purpose='real assertion', level='test', conditions='fixture',
                           source_sha256={p: rs.sha((self.root/p).read_bytes()) for p in self.m['sources']})],
            review_control=dict(existing_results=dict(decision='none', reason='new fixture'), review_round=1, prior_findings=[],
                subagents=dict(decision='not_used', reason='bounded fixture', tasks=[]), criticality_reason='arithmetic fixture only'),
            packet=[dict(path='src/service.py', role='source')]+[dict(path=self.c['plan'], role=r) for r in ['objective', 'findings', 'budget']],
            outside_changes='only fixture files')
        self.write('manifest.json', self.m)
        snapshot = '_workflow/lifecycle/audit'
        w.audit(self.root, 'manifest.json', snapshot, 'review'); w.verify(self.root, snapshot)
        original = self.m
        self.m = copy.deepcopy(original); self.m['outside_changes'] = 'different consumed manifest'
        self.write('other.json', self.m)
        self.write('login/auth.json', {'auth_mode': 'chatgpt', 'tokens': {'fixture': 'test only'}})
        assessment = self.assessment('implementation')
        with self.assertRaisesRegex(ValueError, 'consumed manifest'):
            rp.dispatch(self.root, self.m, 'implementation', sys.executable, self.root/'login',
                        snapshot, 'other.json', assessment)
        self.m = original
        # No gate is mocked: synthetic reviewer output is explicit test data only.
        self.review('implementation')
        w.audit(self.root, 'manifest.json', '_workflow/lifecycle/closeout', 'closeout')
        w.verify(self.root, '_workflow/lifecycle/closeout')

    def test_forged_or_replayed_report_rejected(self):
        out, intent, report, meta = self.review()
        bad = dict(report, request_id='old')
        with self.assertRaisesRegex(ValueError, 'mismatch'):
            rp.validate_report(bad, intent, {}, meta['files'])
        (out / 'report.json').write_bytes(rs.encode(bad))
        with self.assertRaisesRegex(ValueError, 'artifact drift'):
            rp.receipt(out)

    def test_omitted_or_downgraded_findings_block(self):
        _, intent, report, meta = self.review()
        original = self.finding()
        with self.assertRaisesRegex(ValueError, 'omitted/downgraded'):
            rp.validate_report(report, intent, {'F1': original}, meta['files'])
        report['findings'] = [dict(original, severity='suggestion')]
        with self.assertRaisesRegex(ValueError, 'omitted/downgraded'):
            rp.validate_report(report, intent, {'F1': original}, meta['files'])

    def test_latest_failed_review_cannot_reuse_older_pass(self):
        self.review()
        c, reg, local, _ = rp.registered(self.root, self.m)
        rp.reserve(local, reg, 'plan', rp.identity(self.root, self.m, c), 'unknown')
        with self.assertRaises((ValueError, OSError)) as error:
            rp.implement(self.root, self.m)
        self.assertIn('no verified review receipt', str(error.exception))

    def test_eight_requests_including_failed_are_limit(self):
        c, reg, local, _ = rp.registered(self.root, self.m)
        for _ in range(8):
            rp.reserve(local, reg, 'plan', {}, 'x')
        with self.assertRaisesRegex(ValueError, 'budget exhausted'):
            rp.reserve(local, reg, 'plan', {}, 'x')
        self.assertEqual(len(rp.attempts(local)), 8)

    def failed_discovery_attempt(self, *, verdict='blocked', unknowns=None, bad_finding_path=False,
                                 close_finding=False):
        finding = self.finding()
        if close_finding:
            finding['status'] = 'closed'
            finding['closure_evidence'] = ['src/service.py']
        if bad_finding_path:
            finding['paths'] = ['outside.py']
        out, intent, report, meta = self.review(findings=[finding], verdict=verdict)
        report['unknowns'] = ['unresolved dependency'] if unknowns is None else unknowns
        report['discovered_paths'].append('outside.py')
        (out / 'report.json').write_bytes(rs.encode(report))
        events = [dict(type='item.completed', item=dict(type='mcp_tool_call', server='review_snapshot',
                  arguments={'operation': 'read'}, result={'content': []})),
                  dict(type='item.completed', item=dict(type='agent_message', text=json.dumps(report))),
                  dict(type='turn.completed')]
        (out / 'events.jsonl').write_bytes(('\n'.join(json.dumps(e) for e in events)).encode('utf-8'))
        (out / 'review-stdout.log').write_bytes((out / 'events.jsonl').read_bytes())
        (out / 'review-tree-terminal.json').write_bytes(rs.encode(dict(job='fixture', active_processes=0, helper_exit=0)))
        rs.publish(out / 'schema.json', rp.output_schema(intent))
        (out / 'review-stdin.txt').write_bytes((out / 'prompt.txt').read_bytes())
        (out / 'receipt.json').unlink()
        return out, intent, report, meta

    @staticmethod
    def tree_hashes(directory):
        return {p.relative_to(directory).as_posix(): rs.sha(p.read_bytes())
                for p in directory.iterdir() if p.is_file()}

    def test_reconciliation_preserves_raw_failed_request_and_never_gates(self):
        out, intent, report, _ = self.failed_discovery_attempt()
        _, reg, local, _ = rp.registered(self.root, self.m)
        before = self.tree_hashes(out)
        result = rp.reconcile_report(self.root, self.m, '001')
        self.assertEqual(result['status'], rp.RECONCILIATION_STATUS)
        self.assertFalse(result['receipt_written']); self.assertFalse(result['permit_granted'])
        self.assertEqual(before, self.tree_hashes(out), 'reconciliation must leave request bytes untouched')
        self.assertFalse((out / 'receipt.json').exists())
        record_path = local / 'reconciliations/001.json'
        record = rs.load(record_path)
        self.assertEqual(record['omitted_discovered_paths'], ['outside.py'])
        self.assertEqual(record['request_files'], before)
        self.assertEqual(record['status'], rp.RECONCILIATION_STATUS)
        self.assertEqual(rp.prior_findings(local, reg, self.m)['F1'], report['findings'][0])
        with self.assertRaisesRegex(ValueError, 'reconciled failed report.*non-gating'):
            rp.latest(self.root, self.m, 'plan', True)
        replay = rp.reconcile_report(self.root, self.m, '001')
        self.assertEqual(record, rs.load(record_path))
        self.assertEqual(before, self.tree_hashes(out))
        self.assertEqual(replay['status'], rp.RECONCILIATION_STATUS)
        c, _, _, _ = rp.registered(self.root, self.m)
        rp.reserve(local, reg, 'plan', {}, 'later')
        self.assertEqual(rp.prior_findings(local, reg, self.m)['F1'], report['findings'][0])
        snapshot, meta = rp.capture_snapshot(self.root, self.m, c, local, [record_path.relative_to(self.root).as_posix()])
        self.assertIn(record_path.relative_to(self.root).as_posix(), meta['files'])

    def test_prior_unknowns_cannot_be_silently_omitted(self):
        out, _, report, _ = self.failed_discovery_attempt(unknowns=['unknown alpha', 'unknown beta'])
        rp.reconcile_report(self.root, self.m, '001')
        _, reg, local, _ = rp.registered(self.root, self.m)
        inherited = rp.prior_unknowns(local, reg, self.m)
        self.assertEqual({x['text'] for x in inherited}, {'unknown alpha', 'unknown beta'})
        _, intent, current, meta = self.review()
        current['unknowns'] = []
        with self.assertRaisesRegex(ValueError, 'prior unknown obligation omitted'):
            rp.validate_report(current, intent, {}, meta['files'], inherited)
        current['unknowns'] = ['unknown alpha', 'unknown beta']
        current['unknown_dispositions'] = [dict(id=x['id'], text=x['text'], status='open', resolution='', evidence=[])
                                            for x in inherited]
        with self.assertRaisesRegex(ValueError, 'pass has unresolved unknowns'):
            rp.validate_report(current, intent, {}, meta['files'], inherited)
        current['unknowns'] = []
        current['unknown_dispositions'] = [dict(id=x['id'], text=x['text'], status='resolved',
                                                resolution='resolved from frozen source contract',
                                                evidence=['src/storage.py']) for x in inherited]
        rp.validate_report(current, intent, {}, meta['files'], inherited)
        self.assertEqual(rp.validate_unknown_obligations(current, inherited, meta['files']), [])

    def test_reconciliation_rejects_more_than_the_exact_discovery_failure(self):
        out, _, _, _ = self.failed_discovery_attempt(bad_finding_path=True)
        _, _, local, _ = rp.registered(self.root, self.m)
        with self.assertRaisesRegex(ValueError, 'finding needs real source paths'):
            rp.reconcile_report(self.root, self.m, '001')
        self.assertFalse((local / 'reconciliations/001.json').exists())
        self.assertFalse((out / 'receipt.json').exists())

    def test_reconciliation_still_validates_findings_before_recording(self):
        out, _, report, _ = self.failed_discovery_attempt()
        report['findings'][0]['paths'] = ['outside.py']
        (out / 'report.json').write_bytes(rs.encode(report))
        events = [json.loads(line) for line in (out / 'events.jsonl').read_text(encoding='utf-8').splitlines()]
        for event in events:
            if event.get('item', {}).get('type') == 'agent_message':
                event['item']['text'] = json.dumps(report)
        raw_events = ('\n'.join(json.dumps(e) for e in events)).encode('utf-8')
        (out / 'events.jsonl').write_bytes(raw_events)
        (out / 'review-stdout.log').write_bytes(raw_events)
        _, _, local, _ = rp.registered(self.root, self.m)
        with self.assertRaisesRegex(ValueError, 'finding needs real source paths'):
            rp.reconcile_report(self.root, self.m, '001')
        self.assertFalse((local / 'reconciliations/001.json').exists())
        self.assertFalse((out / 'receipt.json').exists())

    def test_validate_report_binds_request_stage_and_snapshot_identity(self):
        _, intent, report, meta = self.review()
        for key in ('request_id', 'stage', 'snapshot_hash'):
            mutated = copy.deepcopy(report); mutated[key] = 'different identity'
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'report request/stage/snapshot mismatch'):
                rp.validate_report(mutated, intent, {}, meta['files'])

    def test_reconciliation_rejects_closed_findings_even_if_normalization_validates(self):
        out, _, _, _ = self.failed_discovery_attempt(close_finding=True)
        _, _, local, _ = rp.registered(self.root, self.m)
        with self.assertRaisesRegex(ValueError, 'cannot consume a report that closes'):
            rp.reconcile_report(self.root, self.m, '001')
        self.assertFalse((local / 'reconciliations/001.json').exists())

    def test_reconciliation_refuses_event_or_snapshot_tampering_and_record_drift(self):
        out, _, _, _ = self.failed_discovery_attempt()
        _, reg, local, _ = rp.registered(self.root, self.m)
        snapshot = next((local / 'snapshots').iterdir())
        original_events = (out / 'events.jsonl').read_bytes()
        events = [json.loads(line) for line in (out / 'events.jsonl').read_text(encoding='utf-8').splitlines()]
        events[1]['item']['text'] = '{}'
        changed_events = ('\n'.join(json.dumps(e) for e in events)).encode('utf-8')
        (out / 'events.jsonl').write_bytes(changed_events)
        (out / 'review-stdout.log').write_bytes(changed_events)
        with self.assertRaisesRegex(ValueError, 'report not present in independent final output'):
            rp.reconcile_report(self.root, self.m, '001')
        (out / 'events.jsonl').write_bytes(original_events)
        (out / 'review-stdout.log').write_bytes(original_events)
        (snapshot / 'src/service.py').write_text('changed frozen source', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'historical frozen source drift'):
            rp.reconcile_report(self.root, self.m, '001')
        (snapshot / 'src/service.py').write_text('from storage import save\n', encoding='utf-8')
        rp.reconcile_report(self.root, self.m, '001')
        record_path = local / 'reconciliations/001.json'
        record = rs.load(record_path); record['gate_effect'] = 'permit'
        record_path.write_bytes(rs.encode(record))
        with self.assertRaisesRegex(ValueError, 'reconciliation evidence drift'):
            rp.prior_findings(local, reg, self.m)

    def test_reconciliation_rejects_pass_even_when_discovery_is_the_only_other_error(self):
        out, _, report, _ = self.failed_discovery_attempt(verdict='pass', unknowns=[])
        _, _, local, _ = rp.registered(self.root, self.m)
        with self.assertRaisesRegex(ValueError, 'only a failed non-pass report'):
            rp.reconcile_report(self.root, self.m, '001')
        self.assertFalse((local / 'reconciliations/001.json').exists())
        self.assertFalse((out / 'receipt.json').exists())

    def test_reconciliation_partial_record_is_never_overwritten(self):
        self.failed_discovery_attempt()
        _, _, local, _ = rp.registered(self.root, self.m)
        record_path = local / 'reconciliations/001.json'
        record_path.parent.mkdir(parents=True)
        partial = b'{"schema_version":'
        record_path.write_bytes(partial)
        with self.assertRaises((ValueError, OSError)):
            rp.reconcile_report(self.root, self.m, '001')
        self.assertEqual(record_path.read_bytes(), partial)

    def test_reconciliation_cannot_be_added_after_a_later_attempt(self):
        self.failed_discovery_attempt()
        _, reg, local, _ = rp.registered(self.root, self.m)
        rp.reserve(local, reg, 'plan', {}, 'later')
        record_path = local / 'reconciliations/001.json'
        prior, old_unknowns = rp.prior_obligations(local, reg, self.m, stop_before='001')
        out = local / 'requests/001'
        with self.assertRaisesRegex(ValueError, 'only the latest failed attempt'):
            rp.reconciliation_evidence(local, out, prior, old_unknowns, self.m, reg, require_latest=True)
        self.assertFalse(record_path.exists())

    def test_fixed_owner_grant_enforces_count_stage_and_intent_binding(self):
        source = '_workflow/fixture/consultation/owner-authorization-2026-09-30.json'
        approval = {'schema_version': 1, 'batch': 'fixture', 'received_at': '2026-09-30 06:13:49 Asia/Shanghai',
                    'owner_prompt': '第 7 次请求计入 8 次上限，请求 001，固定追加 2 次会诊',
                    'owner_reply': '授权两项（推荐）',
                    'authorized_tool_scope': 'non-gating failed-report reconciliation；请求 001 原始文件逐字节不改；不生成成功 receipt；不授予 plan/implementation permit',
                    'additional_consultations': {'count': 2, 'allocations': {'plan': 1, 'implementation': 1},
                       'model_policy': 'gpt-6.1-sol; each request chooses medium/high from current evidence',
                       'acceptance': rp.EXTRA_CONSULTATION_ACCEPTANCE},
                    'limitations': ['No product transaction code may be changed without a valid plan review and implement permit.',
                                    'No true User directory or production gate is authorized.']}
        self.write(source, approval)
        record = rp.record_authorization(self.root, self.m, source)
        _, reg, local, shared = rp.registered(self.root, self.m)
        grant = rp.load_authorization(self.root, self.m, local, shared)
        self.assertEqual(record, grant)
        self.assertEqual(rs.load(shared / 'authorizations/owner-grant.json'), rs.load(local / 'authorizations/owner-grant.json'))
        c, _, _, _ = rp.registered(self.root, self.m)
        _, meta = rp.capture_snapshot(self.root, self.m, c, local,
            [source, (local / 'authorizations/owner-grant.json').relative_to(self.root).as_posix()])
        self.assertIn(source, meta['files'])
        self.assertIn((local / 'authorizations/owner-grant.json').relative_to(self.root).as_posix(), meta['files'])
        for _ in range(8):
            rp.reserve(local, reg, 'plan', {}, 'x')
        out, intent = rp.reserve(local, reg, 'plan', {}, 'x', grant=grant)
        self.assertEqual(intent['extra_authorization']['stage'], 'plan')
        self.assertEqual(intent['extra_authorization']['slot'], 1)
        with self.assertRaisesRegex(ValueError, 'stage allocation exhausted'):
            rp.reserve(local, reg, 'plan', {}, 'x', grant=grant)
        self.assertFalse((local / 'requests/010').exists())
        out, intent = rp.reserve(local, reg, 'implementation', {}, 'x', grant=grant)
        self.assertEqual(intent['extra_authorization']['stage'], 'implementation')
        self.assertEqual(intent['extra_authorization']['slot'], 1)
        with self.assertRaisesRegex(ValueError, 'budget exhausted'):
            rp.reserve(local, reg, 'implementation', {}, 'x', grant=grant)
        _, _, _, _ = rp.registered(self.root, self.m)
        bad = rs.load(local / 'requests/009/intent.json'); bad['extra_authorization']['stage'] = 'implementation'
        (local / 'requests/009/intent.json').write_bytes(rs.encode(bad))
        with self.assertRaisesRegex(ValueError, 'not bound to its allocated owner grant'):
            rp.validate_budget(local, reg, grant)

    def test_authorization_source_and_mirror_drift_fail_closed(self):
        source = '_workflow/fixture/consultation/owner-authorization-2026-09-30.json'
        approval = {'schema_version': 1, 'batch': 'fixture', 'owner_prompt': '第 7 次请求计入 8 次上限，请求 001，追加 2 次会诊',
                    'owner_reply': '授权两项（推荐）',
                    'authorized_tool_scope': 'non-gating failed-report reconciliation；请求 001 原始文件逐字节不改；不生成成功 receipt；不授予 plan/implementation permit',
                    'additional_consultations': {'count': 2, 'allocations': {'plan': 1, 'implementation': 1},
                       'model_policy': 'gpt-6.1-sol; each request chooses medium/high from current evidence',
                       'acceptance': rp.EXTRA_CONSULTATION_ACCEPTANCE},
                    'limitations': ['No product transaction code may be changed without a valid plan review and implement permit.',
                                    'No true User directory or production gate is authorized.']}
        self.write(source, approval)
        rp.record_authorization(self.root, self.m, source)
        _, _, local, shared = rp.registered(self.root, self.m)
        local_record = local / 'authorizations/owner-grant.json'
        shared_record = shared / 'authorizations/owner-grant.json'
        original_local = local_record.read_bytes()
        local_record.write_bytes(rs.encode({'tampered': True}))
        with self.assertRaisesRegex(ValueError, 'mirror drift'):
            rp.load_authorization(self.root, self.m, local, shared)
        local_record.write_bytes(original_local)
        original_source = (self.root / source).read_bytes()
        (self.root / source).write_bytes(original_source + b' ')
        with self.assertRaisesRegex(ValueError, 'record/source drift'):
            rp.load_authorization(self.root, self.m, local, shared)
        (self.root / source).write_bytes(original_source)
        local_record.unlink()
        with self.assertRaisesRegex(ValueError, 'mirror incomplete'):
            rp.load_authorization(self.root, self.m, local, shared)
        rp.record_authorization(self.root, self.m, source)
        grant = rp.load_authorization(self.root, self.m, local, shared)
        approval['additional_consultations']['count'] = 3
        self.write(source, approval)
        with self.assertRaisesRegex(ValueError, 'exactly two fixed'):
            rp.load_authorization(self.root, self.m, local, shared)
        approval['additional_consultations']['count'] = 2
        self.write(source, approval)
        self.assertEqual(grant, rp.load_authorization(self.root, self.m, local, shared))

    def test_removed_config_cannot_revert_adopted_batch(self):
        m = dict(self.m); m.pop('review_process')
        self.assertTrue(rp.required(self.root, m))
        with self.assertRaisesRegex(ValueError, 'cannot revert'):
            rp.gate(self.root, m, 'closeout')

    def test_real_second_process_cannot_take_lock(self):
        shared = rp.location(self.root, self.m)
        script = 'from pathlib import Path; from review_support import lock;\nwith lock(Path(__import__("sys").argv[1])): print("bad")'
        with rs.lock(shared):
            run = subprocess.run([sys.executable, '-B', '-c', script, str(shared)], cwd=Path(rs.__file__).parent,
                                 capture_output=True, text=True)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn('batch locked', run.stderr)

    def test_two_worktrees_share_registration_and_lock(self):
        other = self.root.parent / (self.root.name + '-other')
        subprocess.run(['git', '-C', str(self.root), 'worktree', 'add', '--detach', str(other), 'HEAD'],
                       check=True, capture_output=True)
        self.addCleanup(lambda: subprocess.run(['git', '-C', str(self.root), 'worktree', 'remove', str(other)], capture_output=True))
        self.assertEqual(rp.location(other, self.m), rp.location(self.root, self.m))

    def test_reader_finds_non_navigation_dependency(self):
        c, _, local, _ = rp.registered(self.root, self.m)
        snap, _ = rp.capture_snapshot(self.root, self.m, c, local)
        reader = Reader(snap)
        self.assertIn('src/storage.py', reader.call({'operation': 'list'})['files'])
        self.assertIn('save', reader.call({'operation': 'read', 'query': 'src/storage.py'})['lines'][0])
        with self.assertRaises(ValueError): reader.read('../auth.json')

    def test_capture_rejects_output_recursion_and_secret(self):
        with self.assertRaisesRegex(ValueError, 'output nested'):
            rs.collect(self.root, ['_workflow'], [], ['_workflow/fixture'])
        (self.root / 'src/key.txt').write_text('-----BEGIN ' + 'PRIVATE KEY-----')
        with self.assertRaisesRegex(ValueError, 'credential'):
            rs.collect(self.root, ['src'])

    def test_adoption_is_idempotent_and_keeps_history_budget(self):
        self.m['batch'] = self.c['batch'] = 'adopt-fixture'
        self.m['opening_snapshot'] = '_workflow/adopt-fixture/opening.json'
        self.m['review_process'] = '_workflow/adopt-fixture/config.json'
        self.c['history_reconciliation'] = 'six original requests incl failures, exact source records audited'
        self.c['prior_findings'] = [self.finding()]
        self.write('_workflow/adopt-fixture/history.txt', {'original': 'six attempts plus open F1'})
        self.c['history'] = [dict(request_id=str(i), channel='gpt-tool', outcome='failed',
                                  evidence=['_workflow/adopt-fixture/history.txt']) for i in range(6)]
        self.write(self.m['opening_snapshot'], {'batch': 'adopt-fixture'})
        self.write(self.m['review_process'], self.c)
        first = rp.register(self.root, self.m, 'adopt')
        self.assertEqual(first, rp.register(self.root, self.m, 'adopt'))
        _, reg, local, _ = rp.registered(self.root, self.m)
        for _ in range(2): rp.reserve(local, reg, 'plan', {}, 'x')
        with self.assertRaisesRegex(ValueError, 'budget exhausted'): rp.reserve(local, reg, 'plan', {}, 'x')
        self.assertEqual(reg['prior_findings'][0]['severity'], 'important')
        self.c['history'] = []; self.write(self.m['review_process'], self.c)
        with self.assertRaisesRegex(ValueError, 'immutable'): rp.registered(self.root, self.m)

    def test_source_link_is_rejected(self):
        link = self.root / 'src/link.py'
        try: link.symlink_to(self.root / 'src/service.py')
        except OSError: self.skipTest('symlink privilege unavailable')
        with self.assertRaisesRegex(ValueError, 'link/reparse'): rs.collect(self.root, ['src'])

    def test_deleted_sensitive_path_diff_rejected(self):
        secret = self.root / 'src/auth.json'; secret.write_text('{"token":"fixture"}')
        subprocess.run(['git', '-C', str(self.root), 'add', '--', 'src/auth.json'], check=True)
        with self.assertRaisesRegex(ValueError, 'protected'): rs.git_identity(self.root, ['src'])

    def test_new_begin_cannot_omit_configuration(self):
        import workflow as w
        m = dict(self.m, batch='fresh', opening_snapshot='_workflow/fresh/opening.json',
                 risk_matrix='_workflow/fresh/matrix.json')
        m.pop('review_process')
        self.write(m['risk_matrix'], {'schema_version': 1, 'batch': 'fresh', 'rows': [
            dict(id=k, dimension=k, scenario='scenario', expected='safe', critical=False, status='planned')
            for k in ('state', 'concurrency', 'fault')]})
        self.write('fresh.json', m)
        with self.assertRaisesRegex(ValueError, 'config required'): w.begin(self.root, 'fresh.json')
        self.assertFalse((self.root / m['opening_snapshot']).exists())

    def test_auxiliary_request_counts_but_never_supplies_plan_pass(self):
        ident = rp.reserve_external(self.root, self.m, 'GPT tool', 'small contract check', self.assessment('auxiliary'))
        self.write('_workflow/fixture/external.md', {'report': 'network failure; no verdict'})
        self.write('_workflow/fixture/external-findings.json', [])
        rp.finish_external(self.root, self.m, ident, '_workflow/fixture/external.md', '_workflow/fixture/external-findings.json')
        _, reg, local, _ = rp.registered(self.root, self.m)
        self.assertEqual(len(rp.attempts(local)), 1)
        self.assertEqual(rp.prior_findings(local, reg), {})
        with self.assertRaisesRegex(ValueError, 'missing independent plan'): rp.implement(self.root, self.m)

    def test_incomplete_coverage_and_unknowns_cannot_pass(self):
        _, intent, report, meta = self.review()
        report['unknowns'] = ['missing external caller']
        with self.assertRaisesRegex(ValueError, 'unknowns'): rp.validate_report(report, intent, {}, meta['files'])
        report['unknowns'] = []; report['coverage'].pop('fault_recovery')
        with self.assertRaisesRegex(ValueError, 'coverage'): rp.validate_report(report, intent, {}, meta['files'])

    def test_frozen_snapshot_tamper_rejected(self):
        c, _, local, _ = rp.registered(self.root, self.m)
        snap, _ = rp.capture_snapshot(self.root, self.m, c, local)
        (snap / 'src/service.py').write_text('wrong bytes')
        with self.assertRaisesRegex(ValueError, 'frozen source drift'):
            rp.verify_snapshot(self.root, snap, self.m, c)

    def test_snapshot_input_hash_rechecked_even_if_git_identity_matches(self):
        c, _, local, _ = rp.registered(self.root, self.m)
        snap, meta = rp.capture_snapshot(self.root, self.m, c, local)
        (self.root / 'src/service.py').write_text('tampered while git evidence is held constant\n', encoding='utf-8')
        with patch.object(rp, 'git_identity', return_value=meta['git']):
            with self.assertRaisesRegex(ValueError, 'source/evidence drift'):
                rp.verify_snapshot(self.root, snap, self.m, c)

    def test_uncertain_process_keeps_lock(self):
        shared = rp.location(self.root, self.m)
        with rs.lock(shared): rs.publish(shared / 'recovery-required.json', {'reason': 'uncertain children'})
        self.assertTrue((shared / 'writer.lock').exists())
        with self.assertRaisesRegex(ValueError, 'recovery required'):
            with rs.lock(shared): pass

class ProcessTreeChecks(unittest.TestCase):
    def test_cancellation_and_exception_stop_owned_descendants_keep_lock(self):
        import process_runner as pr
        import os, time
        for exception in [KeyboardInterrupt, RuntimeError]:
            with self.subTest(exception=exception), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); out = root/'out'; out.mkdir(); recovery = root/'recovery'
                ready = root/'ready.txt'; heartbeat = root/'heartbeat.txt'
                script = root/'spawn.py'
                child = 'import pathlib,time\np=pathlib.Path('+repr(str(heartbeat))+')\nwhile True:\n p.write_text(str(time.time()))\n time.sleep(.02)\n'
                script.write_text('import subprocess,sys,pathlib,time\n'+
                    'subprocess.Popen([sys.executable,"-c",'+repr(child)+'])\n'+
                    'pathlib.Path('+repr(str(ready))+').write_text("ready")\ntime.sleep(30)\n')
                original = pr.subprocess.Popen.wait
                interrupted = [False]
                def inject(proc, *args, **kwargs):
                    if not interrupted[0]:
                        deadline = time.monotonic()+10
                        while not heartbeat.exists() and time.monotonic()<deadline: time.sleep(.02)
                        interrupted[0] = True
                        self.assertTrue(ready.exists()); self.assertTrue(heartbeat.exists())
                        raise exception('test cancellation')
                    return original(proc,*args,**kwargs)
                with self.assertRaises(exception), rs.lock(recovery), patch.object(pr.subprocess.Popen,'wait',inject):
                    pr.run([sys.executable,'-B',str(script)],cwd=root,env=os.environ.copy(),directory=out,
                           recovery_directory=recovery,phase='test',timeout=20)
                self.assertTrue((recovery/'writer.lock').exists())
                self.assertTrue((recovery/'inflight.json').exists())
                self.assertTrue((recovery/'recovery-required.json').exists())
                self.assertEqual(rs.load(out/'test-cleanup.json')['active_processes'],0)
                before=heartbeat.read_bytes(); time.sleep(.1); self.assertEqual(before,heartbeat.read_bytes())

class GuardMutations(unittest.TestCase):
    def test_git_materialization_line_endings_preserve_bundle(self):
        here = Path(__file__).parent
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repository = root/'repository'; repository.mkdir()
            subprocess.run(['git', 'init', '-q', str(repository)], check=True)
            for relative in rs.BUNDLE_FILES:
                target = repository/'tools/mistletoe'/relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((here/relative).read_bytes())
            (repository/'tools/mistletoe/review-bundle.json').write_bytes((here/'review-bundle.json').read_bytes())
            subprocess.run(['git', '-C', str(repository), '-c', 'core.autocrlf=true', 'add', '--', 'tools/mistletoe', 'Docs/design'],
                           check=True, capture_output=True)
            subprocess.run(['git', '-C', str(repository), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                            'commit', '--only', '-qm', 'fixture', '--', 'tools/mistletoe', 'Docs/design'], check=True, capture_output=True)
            for line_endings in ['true', 'false']:
                checkout = root/line_endings
                subprocess.run(['git', '-c', 'core.autocrlf='+line_endings, 'clone', '-q', str(repository), str(checkout)],
                               check=True, capture_output=True)
                result = subprocess.run([sys.executable, '-B', str(checkout/'tools/mistletoe/review_process.py'), 'verify-bundle'],
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stdout+result.stderr)

    def test_clean_worktree_bundle_and_missing_file(self):
        here = Path(__file__).parent
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp) / 'tools/mistletoe'; base.mkdir(parents=True)
            for rel in rs.BUNDLE_FILES:
                dest = base / rel; dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes((here / rel).read_bytes())
            (base / 'review-bundle.json').write_bytes((here / 'review-bundle.json').read_bytes())
            cmd = [sys.executable, '-B', str(base / 'review_process.py'), 'verify-bundle']
            good = subprocess.run(cmd, capture_output=True, text=True)
            self.assertEqual(good.returncode, 0, good.stdout + good.stderr)
            (base / 'templates/review-plan.json').unlink()
            bad = subprocess.run(cmd, capture_output=True, text=True)
            self.assertEqual(bad.returncode, 2)

    def test_critical_guards_have_discriminating_tests(self):
        here = Path(__file__).parent
        cases = [
            ('review_process.py', "'contracts': {p: sha(safe_bytes(root, p)) for p in c.get('extra_files', [])}", "'contracts': {}",
             'ProcessChecks.test_external_contract_change_invalidates_permit'),
            ('review_process.py', "intent.get('manifest_digest') == manifest_digest(manifest)", 'True',
             'ProcessChecks.test_manifest_changes_cannot_borrow_same_batch_implementation_pass'),
            ('review_process.py', "a.get('model') == model and a.get('effort') == effort", 'True',
             'ProcessChecks.test_model_assessment_rejects_stale_missing_and_contradiction'),
            ('execution_evidence.py', "conditions(b, refs[0]) == conditions(m, refs[1]) == conditions(restored, refs[2])", 'True',
             'ExecutionChecks.test_mutation_changed_filter_arguments_rejected'),
            ('execution_evidence.py', "path(root, record['patch']).read_bytes() == canonical_patch and canonical_patch", 'True',
             'ExecutionChecks.test_patch_must_describe_actual_executed_bytes'),
            ('execution_evidence.py', "r['purpose'] == 'current_regression' and r['exit_codes']['run'] == 0 and w.successful(trx)", 'True',
             'ExecutionChecks.test_authenticated_historical_comparison_and_missing_baseline'),
            ('review_support.py', "if not (directory / 'recovery-required.json').exists() and not (directory / 'inflight.json').exists():", 'if True:',
             'ProcessTreeChecks.test_cancellation_and_exception_stop_owned_descendants_keep_lock'),
            ('review_process.py', 'require(not blockers,', 'require(True,', 'ProcessChecks.test_plan_pass_with_plan_important_is_rejected'),
            ('review_process.py', "require(total < BASE_CONSULTATION_LIMIT + (grant['count'] if grant else 0),\n            'consultation budget exhausted; owner bounded authorization required')",
             "require(True, 'consultation budget exhausted; owner bounded authorization required')",
             'ProcessChecks.test_eight_requests_including_failed_are_limit'),
            ('review_process.py', "require(not report['unknowns'],", 'require(True,', 'ProcessChecks.test_incomplete_coverage_and_unknowns_cannot_pass'),
            ('review_process.py', "report.get('verdict') in {'blocked', 'changes_required'} and report.get('unknowns')",
             'True', 'ProcessChecks.test_reconciliation_rejects_pass_even_when_discovery_is_the_only_other_error'),
            ('review_process.py', "validate_report(report, intent, prior, meta['files'], prior_unknowns)",
             'pass', 'ProcessChecks.test_reconciliation_still_validates_findings_before_recording'),
            ('review_process.py', "def validate_report(report, intent, prior, files, prior_unknowns=()):\n    require(report.get('request_id') == intent['request_id'] and report.get('stage') == intent['stage']\n            and report.get('snapshot_hash') == intent['snapshot_hash'], 'report request/stage/snapshot mismatch')",
             "def validate_report(report, intent, prior, files, prior_unknowns=()):\n    require(True, 'report request/stage/snapshot mismatch')",
             'ProcessChecks.test_validate_report_binds_request_stage_and_snapshot_identity'),
            ('review_process.py', "    require(set(mapped) == set(by_id), 'prior unknown obligation omitted from disposition list')\n    current = [dict(item) for item in reported]\n    for old in prior_unknowns:\n        disposition = mapped[old['id']]",
             "    require(True, 'prior unknown obligation omitted from disposition list')\n    current = [dict(item) for item in reported]\n    for old in prior_unknowns:\n        disposition = mapped.get(old['id'], {'id': old['id'], 'text': old['text'], 'status': 'resolved'})",
             'ProcessChecks.test_prior_unknowns_cannot_be_silently_omitted'),
            ('review_process.py', "any(_same_json(text, report) for text in finals)", 'True',
             'ProcessChecks.test_reconciliation_refuses_event_or_snapshot_tampering_and_record_drift'),
            ('review_process.py', "require(sha(safe_bytes(snapshot, rel)) == expected, 'historical frozen source drift')",
             "require(True, 'historical frozen source drift')",
             'ProcessChecks.test_reconciliation_refuses_event_or_snapshot_tampering_and_record_drift'),
            ('review_process.py', "return sorted(refs), sorted(historical)", 'return [], []',
             'ProcessChecks.test_reconciled_request_and_historical_snapshot_are_frozen_for_next_review'),
            ('review_process.py', "relevant_refs = [p for p in refs if not any(part in ('/' + p) for part in generated)]",
             'relevant_refs = list(refs)', 'ProcessChecks.test_full_workspace_status_is_distinct_from_declared_scope_diff'),
            ('review_process.py', 'publish(record_path, record)', "publish(record_path, record)\n            (out / 'report.json').write_bytes(b'{}')",
             'ProcessChecks.test_reconciliation_preserves_raw_failed_request_and_never_gates'),
            ('review_process.py', "            'receipt_written': False, 'permit_granted': False,",
             "            'receipt_written': True, 'permit_granted': True,",
             'ProcessChecks.test_reconciliation_preserves_raw_failed_request_and_never_gates'),
            ('review_process.py', "    return {'request': request, 'status': RECONCILIATION_STATUS,\n            'receipt_written': False,",
             "    (out / 'receipt.json').write_bytes(b'{}')\n    return {'request': request, 'status': RECONCILIATION_STATUS,\n            'receipt_written': False,",
             'ProcessChecks.test_reconciliation_preserves_raw_failed_request_and_never_gates'),
            ('review_process.py', "        if record_path.exists():\n            require(load(record_path) == record, 'reconciliation record drift; never overwrite')\n        else:\n            publish(record_path, record)",
             "        record_path.write_bytes(encode(record))", 'ProcessChecks.test_reconciliation_partial_record_is_never_overwritten'),
            ('review_process.py', "    if require_latest:\n        require(ledger[-1] == out, 'only the latest failed attempt may be reconciled')",
             "    if require_latest:\n        pass",
             'ProcessChecks.test_reconciliation_cannot_be_added_after_a_later_attempt'),
            ('review_process.py', "if not (p / 'receipt.json').exists():", 'if False:',
             'ProcessChecks.test_latest_failed_review_cannot_reuse_older_pass'),
            ('review_process.py', "require(slot <= grant['allocations'][stage], 'owner authorization stage allocation exhausted')",
             "require(True, 'owner authorization stage allocation exhausted')",
             'ProcessChecks.test_fixed_owner_grant_enforces_count_stage_and_intent_binding'),
            ('review_process.py', "require(shared_record == local_record, 'owner authorization mirror drift')",
             "require(True, 'owner authorization mirror drift')",
             'ProcessChecks.test_authorization_source_and_mirror_drift_fail_closed'),
            ('review_process.py', "require(shared_record == expected, 'owner authorization record/source drift')",
             "require(True, 'owner authorization record/source drift')",
             'ProcessChecks.test_authorization_source_and_mirror_drift_fail_closed'),
            ('review_process.py', '*manifest_artifact_paths(root, manifest, manifest_path)', '*[]',
             'ProcessChecks.test_dispatch_transport_contract_and_current_model'),
            ('review_process.py', "hashes(snapshot_inputs(root, m['roots'], m['refs'], state_dir(root, manifest), historical_refs)) == m['source_files']", 'True',
             'ProcessChecks.test_snapshot_input_hash_rechecked_even_if_git_identity_matches'),
            ('execution_evidence.py', "require(inputs(root, recipe) == before,", 'require(True,', 'ExecutionChecks.test_input_changed_during_execution_not_certified'),
        ]
        for file, before, after, test in cases:
            with self.subTest(test=test), tempfile.TemporaryDirectory() as temp:
                dest = Path(temp)
                for p in here.glob('*.py'): (dest / p.name).write_bytes(p.read_bytes())
                text = (dest / file).read_text(encoding='utf-8-sig')
                self.assertEqual(text.count(before), 1)
                text = text.replace(before, after); compile(text, file, 'exec')
                (dest / file).write_text(text, encoding='utf-8')
                run = subprocess.run([sys.executable, '-B', '-m', 'unittest', 'test_review_process.'+test],
                                     cwd=dest, capture_output=True, text=True)
                self.assertNotEqual(run.returncode, 0)
                self.assertIn('FAIL:', run.stderr)
                self.assertNotIn('ERROR:', run.stderr)

class ExecutionChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve(); (self.root / 'src').mkdir()
        (self.root / 'src/run.py').write_text('import pathlib,sys\npathlib.Path(sys.argv[1]).write_text("result")\n')
        self.recipe = {'purpose': 'current_regression', 'conditions': 'fixture subprocess', 'input_roots': ['src'], 'input_files': [],
                       'run_argv': [sys.executable, '{root}/src/run.py', '{out}/result.txt'], 'products': [], 'results': ['result.txt']}
        self.save()
        p = patch.object(ee, 'verify_bundle', return_value='bundle'); p.start(); self.addCleanup(p.stop)

    def save(self):
        (self.root / 'recipe.json').write_bytes(rs.encode(self.recipe))

    def capture(self):
        return ee.capture(self.root, 'recipe.json', '_workflow/fixture/executions')

    def test_real_execution_binds_version_and_output(self):
        ref = self.capture(); ee.validate(self.root, ref, purpose='current_regression')
        (self.root / 'src/other.py').write_text('new input')
        with self.assertRaisesRegex(ValueError, 'source drift'):
            ee.validate(self.root, ref, purpose='current_regression')

    def test_real_nonzero_can_be_provenance_but_not_green(self):
        (self.root / 'src/run.py').write_text('import pathlib,sys\npathlib.Path(sys.argv[1]).write_text("failed assertion")\nsys.exit(1)\n')
        self.recipe['purpose'] = 'negative_test'; self.save()
        ref = self.capture()
        self.assertEqual(ee.validate(self.root, ref, purpose='negative_test')['exit_codes']['run'], 1)
        with self.assertRaisesRegex(ValueError, 'green'):
            ee.validate(self.root, ref, purpose='current_regression')

    def test_old_binary_path_rejected_before_run(self):
        self.recipe.update(build_argv=[sys.executable, '{root}/src/run.py', '{out}/new.exe'], products=['new.exe'])
        self.save()
        with self.assertRaisesRegex(ValueError, 'fresh product'):
            self.capture()

    def test_fresh_direct_executable_can_run_after_build(self):
        import os
        dll = f'python{sys.version_info.major}{sys.version_info.minor}.dll'
        (self.root / 'src/build.py').write_text('import pathlib,shutil,sys\nout=pathlib.Path(sys.argv[1])\nshutil.copy2(sys.executable,out/"runner.exe")\n'
            + 'shutil.copy2(pathlib.Path(sys.base_prefix)/' + repr(dll) + ',out/' + repr(dll) + ')\n')
        self.recipe.update(build_argv=[sys.executable, '{root}/src/build.py', '{out}'], products=['runner.exe', dll],
                           environment={'PYTHONHOME': sys.base_prefix})
        self.recipe['run_argv'] = ['{out}/runner.exe', '-B', '{root}/src/run.py', '{out}/result.txt']
        self.save()
        ref = self.capture()
        self.assertEqual(ee.validate(self.root, ref, purpose='current_regression')['exit_codes']['run'], 0)

    def test_input_changed_during_execution_not_certified(self):
        (self.root / 'src/run.py').write_text('import pathlib,sys\npathlib.Path(sys.argv[1]).write_text("result")\npathlib.Path(__file__).write_text("changed")\n')
        with self.assertRaisesRegex(ValueError, 'input drift'):
            self.capture()

    def test_build_failure_cannot_be_mutation_kill(self):
        self.recipe.update(build_argv=[sys.executable, '-c', 'import sys;sys.exit(1)', '{out}'],
                           products=['new.py'], run_argv=[sys.executable, '{out}/new.py'])
        self.save()
        with self.assertRaisesRegex(ValueError, 'build failed'):
            self.capture()

    def make_b_m_b(self, changed_condition=None):
        import workflow as w
        from test_workflow import trx
        good = trx([('t', 'target', 'Passed')])
        bad = trx([('t', 'target', 'Failed', 'counterexample', 'target assertion')])
        behavior = self.root / 'src/behavior.py'; behavior.write_text('good = True\n')
        original = behavior.read_bytes()
        (self.root / 'src/run.py').write_text(
            'import pathlib,sys,uuid\nexec(pathlib.Path(__file__).with_name("behavior.py").read_text())\n'
            + 'data = ' + repr(good) + ' if good else ' + repr(bad) + '\n'
            + 'data = data.replace(b"exec-0", uuid.uuid4().hex.encode())\n'
            + 'pathlib.Path(sys.argv[1]).write_bytes(data)\nsys.exit(0 if good else 1)\n')
        self.recipe.update(purpose='mutation', run_argv=[sys.executable, '-B', '{root}/src/run.py', '{out}/result.trx'], results=['result.trx'])
        self.save(); baseline = self.capture()
        behavior.write_text('good = False\n'); mutant_bytes = behavior.read_bytes(); mutant_hash = rs.sha(mutant_bytes)
        if changed_condition == 'environment':
            with patch.dict(__import__('os').environ, {'MISTLETOE_TEST_FILTER': 'other'}): mutant = self.capture()
        elif changed_condition == 'arguments':
            altered = dict(self.recipe, run_argv=self.recipe['run_argv'] + ['--other-filter'])
            (self.root / 'mutant-recipe.json').write_bytes(rs.encode(altered))
            mutant = ee.capture(self.root, 'mutant-recipe.json', '_workflow/fixture/executions')
        else:
            mutant = self.capture()
        behavior.write_bytes(original); restored = self.capture()
        current = dict(self.recipe, purpose='current_regression')
        (self.root / 'current.json').write_bytes(rs.encode(current))
        final = ee.capture(self.root, 'current.json', '_workflow/fixture/executions')
        result = lambda ref: (Path(ref).parent / 'result.trx').as_posix()
        import difflib
        patch_file = self.root / 'mutation.patch'; patch_file.write_bytes(''.join(difflib.unified_diff(
            original.decode().splitlines(keepends=True), mutant_bytes.decode().splitlines(keepends=True), fromfile='src/behavior.py', tofile='src/behavior.py')).encode())
        record = dict(id='M1', source='src/behavior.py', original_sha256=rs.sha(original), restored_sha256=rs.sha(original),
                      mutant_sha256=mutant_hash, baseline_execution=baseline, mutant_execution=mutant, restored_execution=restored,
                      baseline_trx=result(baseline), mutant_trx=result(mutant), restored_trx=result(restored),
                      baseline_exit=0, mutant_exit=1, restored_exit=0, build_exit=0,
                      build_log=(Path(baseline).parent / 'run.log').as_posix(),
                      target_test_id='t', target_name='target', failure_contains='counterexample', assertion_contains='target assertion',
                      patch='mutation.patch', patch_sha256=rs.sha(patch_file.read_bytes()))
        manifest = dict(sources=['src/behavior.py'], tests=[{'path': result(final), 'expect_success': True}],
                        execution_evidence=[final], mutations=[record])
        return manifest, record

    def test_real_b_m_b_receipts_and_replay_rejection(self):
        import workflow as w
        manifest, record = self.make_b_m_b()
        ee.validate_manifest(self.root, manifest)
        w.mutation_check(record, lambda p: (self.root/p).read_bytes(), lambda p: w.parse_trx((self.root/p).read_bytes()))
        record['mutant_trx'] = record['baseline_trx']
        with self.assertRaisesRegex(ValueError, 'result replay'): ee.validate_manifest(self.root, manifest)

    def test_mutation_changed_environment_rejected(self):
        manifest, _ = self.make_b_m_b('environment')
        with self.assertRaisesRegex(ValueError, 'execution conditions differ'): ee.validate_manifest(self.root, manifest)

    def test_mutation_changed_filter_arguments_rejected(self):
        manifest, _ = self.make_b_m_b('arguments')
        with self.assertRaisesRegex(ValueError, 'execution conditions differ'): ee.validate_manifest(self.root, manifest)

    def test_patch_must_describe_actual_executed_bytes(self):
        manifest, record = self.make_b_m_b()
        (self.root / 'mutation.patch').write_text('different patch')
        record['patch_sha256'] = rs.sha((self.root / 'mutation.patch').read_bytes())
        with self.assertRaisesRegex(ValueError, 'executed bytes'): ee.validate_manifest(self.root, manifest)

    def test_authenticated_historical_comparison_and_missing_baseline(self):
        from test_workflow import trx
        data = trx([('t', 'target', 'Passed')])
        script = 'import pathlib,sys\npathlib.Path(sys.argv[1]).write_bytes(' + repr(data) + ')\n'
        (self.root / 'src/run.py').write_text(script)
        self.recipe.update(purpose='historical_baseline', run_argv=[sys.executable, '-B', '{root}/src/run.py', '{out}/result.trx'], results=['result.trx'])
        self.save(); baseline = self.capture()
        (self.root / 'src/run.py').write_text(script + '# implementation B\n')
        current = dict(self.recipe, purpose='current_regression')
        (self.root / 'current.json').write_bytes(rs.encode(current))
        final = ee.capture(self.root, 'current.json', '_workflow/fixture/executions')
        result = lambda ref: (Path(ref).parent / 'result.trx').as_posix()
        manifest = dict(sources=['src/run.py'], tests=[{'path': result(final), 'expect_success': True}],
                        execution_evidence=[baseline, final], mutations=[], comparison={'baseline': result(baseline), 'final': result(final)})
        ee.validate_manifest(self.root, manifest)
        manifest['execution_evidence'] = [final]
        with self.assertRaisesRegex(ValueError, 'comparison result lacks'): ee.validate_manifest(self.root, manifest)
        manifest['execution_evidence'] = [baseline, final]; manifest['tests'][0]['path'] = result(baseline)
        with self.assertRaisesRegex(ValueError, 'cannot prove green'): ee.validate_manifest(self.root, manifest)

if __name__ == '__main__':
    unittest.main()
