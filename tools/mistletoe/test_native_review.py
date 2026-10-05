"""Discriminating tests of native evidence identity, scope and stage gates.

Synthetic rollout fixtures test parsing only; actual model runs are kept in the
batch's independent evidence and are never claimed by these unit tests.
"""
import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import native_review as n


class NativeReviewTests(unittest.TestCase):
    def setUp(self):
        self.base = Path(tempfile.mkdtemp(prefix='native-review-test-'))
        self.root = self.base / 'repo'; self.root.mkdir()
        self.write('src/A.cs', 'class A { public int Run() => B.Value; }\n')
        self.write('other/B.cs', 'class B { public const int Value = 1; }\n')
        self.git('init', '-q'); self.git('config', 'user.name', 'Native fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.git('add', '--', 'src/A.cs', 'other/B.cs')
        self.git('-c', 'commit.gpgsign=false', 'commit', '--only', '-qm', 'fixture', '--', 'src/A.cs', 'other/B.cs')
        self.write('_workflow/b/plan.json', {'goal': 'fixture'})
        self.write('_workflow/b/policy.json', {'source_thread': 'parent', 'request_cap_stop_disabled_for_this_goal': True})
        self.config = {'batch': 'fixture', 'state': '_workflow/b/native', 'plan': '_workflow/b/plan.json',
                       'owner_policy': '_workflow/b/policy.json', 'parent_thread': 'parent',
                       'snapshot_base': str(self.base / 'snapshots'), 'prior_files': [],
                       'write_paths': ['src/A.cs']}
        self.write('_workflow/b/config.json', self.config)
        self.assessment = {'model': 'gpt-6.1-sol', 'effort': 'high', 'reason': 'identity/state risk',
                           'risk_unresolved': True, 'dimensions': {k: 'fixture evidence' for k in n.DIMENSIONS}}
        self.write('_workflow/b/assessment.json', self.assessment)

    def checkpoint_prior_fixture(self):
        self.prior(unknown=True)
        out = self.prepare()
        originals = [self.finding()]
        unknowns = ["original uncertainty"]
        for number in (1, 2):
            finding = self.finding()
            # Same ID, distinct original objects must retain distinct keys.
            finding["counterexample"] = "checkpoint-" + str(number)
            text = "checkpoint uncertainty " + str(number)
            parent = n.sha((out / "checkpoints/1.json").read_bytes()) \
                if number == 2 else None
            report = self.report(
                out, kind="checkpoint", ordinal=number,
                parent_checkpoint_sha256=parent,
                remaining_queue=["other/B.cs"],
                findings=[finding], unknowns=[text],
            )
            n.checkpoint(out, self.rollout(out, report), number)
            originals.append(finding)
            unknowns.append(text)
        return out, originals, unknowns

    def test_prepare_with_canonical_checkpoints_and_derived_priors_preserves_full_original_ledger(self):
        out, originals, unknowns = self.checkpoint_prior_fixture()
        before = {p.relative_to(out).as_posix(): n.regular(p)
                  for p in out.rglob("*") if p.is_file()}

        newer = self.prepare()  # Current implementation reaches the N24 fault.
        prior = n.load(newer / "prior.json")
        expected_findings = {n.sha(n.encode(f)): f for f in originals}
        expected_unknowns = {n.sha(t.encode("utf-8")): t for t in unknowns}
        self.assertEqual(
            {k: row["original"] for k, row in prior["findings"].items()},
            expected_findings,
        )
        self.assertEqual(
            {k: row["original"] for k, row in prior["unknowns"].items()},
            expected_unknowns,
        )
        self.assertEqual(len(prior["findings"]), 3)
        self.assertEqual(before, {
            p.relative_to(out).as_posix(): n.regular(p)
            for p in out.rglob("*") if p.is_file()
        })
        _, snapshot = n.verify_input(newer, current=True)
        for name in ("1.json", "1-prior.json", "1-source.json",
                     "1-native-rollout.jsonl",
                     "2.json", "2-prior.json", "2-source.json",
                     "2-native-rollout.jsonl"):
            path = out / "checkpoints" / name
            relative = path.relative_to(self.root.resolve()).as_posix()
            self.assertIn(relative, snapshot["input"]["extra"])
            self.assertEqual(
                (Path(snapshot["contracts"]) / relative).read_bytes(),
                path.read_bytes(),
            )

    def test_prepare_rejects_changed_or_missing_derived_original_source(self):
        out, _, _ = self.checkpoint_prior_fixture()
        source = self.root / "_workflow/b/old-report.json"
        original = source.read_bytes()
        for mode in ("changed", "missing"):
            with self.subTest(mode=mode):
                try:
                    if mode == "changed":
                        source.write_bytes(original + b"\n")
                    else:
                        source.unlink()  # Only this fixture's temporary source.
                    with self.assertRaisesRegex(
                        n.Blocked, "derived prior source (SHA mismatch|missing)"
                    ):
                        self.prepare()
                finally:
                    source.write_bytes(original)

    def test_prepare_rejects_derived_original_object_or_key_drift(self):
        out, _, _ = self.checkpoint_prior_fixture()
        path = out / "checkpoints/2-prior.json"
        original = path.read_bytes()
        ledger = n.load(path)
        key = next(iter(ledger["findings"]))
        ledger["findings"][key]["original"]["counterexample"] = "changed"
        try:
            path.write_bytes(n.encode(ledger))
            with self.assertRaisesRegex(
                n.Blocked, "derived original omitted/changed"
            ):
                self.prepare()
        finally:
            path.write_bytes(original)

    def write(self, rel, value):
        p = self.root / rel; p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(n.encode(value) if isinstance(value, dict) else value.encode('utf-8'))
        return p

    def git(self, *args):
        p = subprocess.run(['git', '-C', str(self.root), *args], capture_output=True)
        self.assertEqual(p.returncode, 0, p.stderr.decode('utf-8', errors='replace'))
        return p.stdout

    def prepare(self, stage='plan'):
        result = n.prepare(self.root, '_workflow/b/config.json', stage, '_workflow/b/assessment.json', '/root/reviewer')
        return Path(result['request'])

    def report(self, out, **updates):
        q = n.load(out / 'request.json')
        r = {k: q[k] for k in ('request_id', 'stage', 'snapshot_hash')}
        r.update(verdict='pass', findings=[], unknowns=[], coverage={'scope': 'fixture'},
                 read_paths=['src/A.cs', 'other/B.cs'], prior_dispositions=[])
        r.update(updates); return r

    def rollout(self, out, report=None, **updates):
        q = n.load(out / 'request.json'); report = self.report(out) if report is None else report
        text = json.dumps(report, ensure_ascii=False)
        spawn = {'parent_thread_id': q['parent_thread'], 'agent_path': q['agent_path'], 'depth': 1}
        context = {'model': q['assessment']['model'], 'effort': q['assessment']['effort'], 'turn_id': 'fixture-turn'}
        spawn.update(updates.get('spawn', {})); context.update(updates.get('context', {}))
        items = [
            {'type': 'session_meta', 'payload': {'id': 'fixture-session', 'source': {'subagent': {'thread_spawn': spawn}}}},
            {'type': 'turn_context', 'timestamp': n.datetime.now(n.timezone.utc).isoformat(), 'payload': context},
            {'type': 'response_item', 'payload': {'type': 'custom_tool_call', 'name': 'exec', 'call_id': 'read1', 'input': 'tools.exec_command({cmd:"Get-Content src/A.cs"})'}},
            {'type': 'response_item', 'payload': {'type': 'custom_tool_call_output', 'call_id': 'read1', 'output': 'fixture source'}},
            {'type': 'event_msg', 'payload': {'type': 'item_completed', 'turn_id': 'fixture-turn',
                'item': {'type': 'CommandExecution', 'status': 'completed', 'exit_code': 0,
                         'cwd': n.load(out / 'snapshot.json')['source'], 'stderr': '', 'stdout': 'class A {}',
                         'command': ['pwsh', '-Command', "Get-Content -LiteralPath 'src/A.cs'"]}}},
            {'type': 'event_msg', 'payload': {'type': 'item_completed', 'turn_id': 'fixture-turn',
                'item': {'type': 'AgentMessage', 'id': 'final-fixture', 'phase': 'final_answer'}}},
            {'type': 'response_item', 'payload': {'type': 'message', 'id': 'final-fixture', 'role': 'assistant', 'phase': 'final_answer', 'content': [{'type': 'output_text', 'text': text}]}},
        ]
        if not updates.get('incomplete'):
            items.append({'type': 'event_msg', 'payload': {'type': 'task_complete', 'turn_id': 'fixture-turn', 'last_agent_message': text}})
        p = self.base / ('rollout-' + n.uuid.uuid4().hex + '.jsonl')
        p.write_text('\n'.join(json.dumps(i, ensure_ascii=False) for i in items) + '\n', encoding='utf-8')
        return p

    def test_whole_project_ignored_and_untracked_source_are_readable(self):
        self.write('.gitignore', 'ignored/\n'); self.write('ignored/C.cs', 'class C {}')
        self.write('untracked/D.cs', 'class D {}'); out = self.prepare()
        q, s = n.verify_input(out, True)
        self.assertIn('other/B.cs', s['input']['files']); self.assertIn('ignored/C.cs', s['input']['files'])
        self.assertIn('untracked/D.cs', s['input']['files']); self.assertTrue((Path(s['source']) / 'other/B.cs').is_file())
        self.assertNotIn('_workflow/b/config.json', s['input']['files'])
        self.assertIn('_workflow/b/config.json', s['input']['extra'])

    def test_protected_runtime_and_credentials_are_not_copied(self):
        for p in ['User/private.cs', 'bin/X.cs', '.codex/config.toml', '.env', 'nested/auth.json']:
            self.write(p, 'private')
        out = self.prepare(); _, s = n.verify_input(out)
        self.assertFalse(any(p in s['input']['files'] for p in ['User/private.cs', 'bin/X.cs', '.codex/config.toml', '.env', 'nested/auth.json']))

    def test_unread_file_drift_invalidates_capture(self):
        out = self.prepare(); s = n.load(out / 'snapshot.json')
        (Path(s['source']) / 'other/B.cs').write_text('changed', encoding='utf-8')
        with self.assertRaisesRegex(n.Blocked, 'full frozen tree drift'): n.verify_input(out)

    def test_add_delete_and_rename_invalidate_complete_tree(self):
        for action in ['add', 'delete', 'rename']:
            with self.subTest(action=action):
                out = self.prepare(); source = Path(n.load(out / 'snapshot.json')['source'])
                if action == 'add': (source / 'new.cs').write_text('new', encoding='utf-8')
                elif action == 'delete': (source / 'other/B.cs').unlink()
                else: (source / 'other/B.cs').rename(source / 'other/Renamed.cs')
                with self.assertRaises(n.Blocked): n.verify_input(out)

    def test_current_project_drift_invalidates_receipt(self):
        out = self.prepare(); n.capture(out, self.rollout(out))
        self.write('other/B.cs', 'changed')
        with self.assertRaisesRegex(n.Blocked, 'current project'): n.receipt(out, True)

    def test_head_change_invalidates_receipt(self):
        out = self.prepare(); n.capture(out, self.rollout(out))
        self.write('other/B.cs', 'changed'); self.git('add', '--', 'other/B.cs')
        self.git('-c', 'commit.gpgsign=false', 'commit', '--only', '-qm', 'changed fixture', '--', 'other/B.cs')
        with self.assertRaisesRegex(n.Blocked, 'current project'): n.receipt(out, True)

    def test_valid_native_source_is_distinct_from_cli_receipt(self):
        out = self.prepare(); n.capture(out, self.rollout(out)); q, r = n.receipt(out, True)
        self.assertEqual(r['verdict'], 'pass'); saved = n.load(out / 'receipt.json')
        self.assertNotIn('exit_code', saved); self.assertNotIn('job', saved)
        self.assertEqual(saved['native_identity']['observed_config'][0]['model'], 'gpt-6.1-sol')

    def test_wrong_parent_model_effort_and_agent_are_rejected(self):
        for kw in [{'spawn': {'parent_thread_id': 'other'}}, {'spawn': {'agent_path': '/root/other'}},
                   {'context': {'model': 'other'}}, {'context': {'effort': 'medium'}}]:
            with self.subTest(kw=kw):
                out = self.prepare()
                with self.assertRaises(n.Blocked): n.capture(out, self.rollout(out, **kw))
                self.assertFalse((out / 'receipt.json').exists())

    def test_partial_final_and_wrong_completion_do_not_grant_permission(self):
        out = self.prepare()
        with self.assertRaisesRegex(n.Blocked, 'matching completed'): n.capture(out, self.rollout(out, incomplete=True))
        self.assertFalse((out / 'receipt.json').exists())

    def test_replay_and_stage_snapshot_mismatch_are_rejected(self):
        for key, value in [('request_id', 'old'), ('stage', 'implementation'), ('snapshot_hash', 'old')]:
            with self.subTest(key=key):
                out = self.prepare(); r = self.report(out); r[key] = value
                with self.assertRaises(n.Blocked): n.capture(out, self.rollout(out, r))
                self.assertFalse((out / 'receipt.json').exists())

    def test_parent_rewriting_saved_report_is_detected(self):
        out = self.prepare(); n.capture(out, self.rollout(out))
        r = n.load(out / 'report.json'); r['coverage']['scope'] = 'rewritten'
        (out / 'report.json').write_bytes(n.encode(r))
        with self.assertRaisesRegex(n.Blocked, 'artifact drift'): n.receipt(out)

    def finding(self, obligation='implementation'):
        return {'id': 'OLD-1', 'severity': 'must', 'obligation': obligation, 'status': 'open',
                'root_cause': 'outside dependency', 'counterexample': 'fixture', 'paths': ['other/B.cs'],
                'repair_steps': 'repair', 'tests': 'test', 'closure_evidence': []}

    def prior(self, obligation='implementation', unknown=False):
        doc = {'findings': [self.finding(obligation)], 'unknowns': ['original uncertainty'] if unknown else []}
        self.write('_workflow/b/old-report.json', doc); self.config['prior_files'] = ['_workflow/b/old-report.json']
        self.write('_workflow/b/config.json', self.config)

    def disposition(self, out, **updates):
        prior = n.load(out / 'prior.json'); key = next(iter(prior['findings']))
        d = {'key': key, 'id': 'OLD-1', 'severity': 'must', 'obligation': prior['findings'][key]['original']['obligation'],
             'disposition': 'repair_planned', 'reason': 'reviewed repair plan', 'evidence': ['other/B.cs']}
        d.update(updates); return d

    def test_plan_can_preserve_open_implementation_obligation(self):
        self.prior(); out = self.prepare(); r = self.report(out, findings=[self.finding()], prior_dispositions=[self.disposition(out)])
        n.capture(out, self.rollout(out, r)); self.assertEqual(n.receipt(out)[1]['verdict'], 'pass')

    def test_plan_cannot_falsely_close_implementation(self):
        self.prior(); out = self.prepare(); r = self.report(out, prior_dispositions=[self.disposition(out, disposition='resolved_with_evidence')])
        with self.assertRaisesRegex(n.Blocked, 'plan cannot close'): n.capture(out, self.rollout(out, r))

    def test_omission_downgrade_and_original_obligation_change_rejected(self):
        self.prior()
        for ds in [[], [{'severity': 'suggestion'}], [{'obligation': 'plan'}]]:
            out = self.prepare(); dispositions = [] if not ds else [self.disposition(out, **ds[0])]
            r = self.report(out, prior_dispositions=dispositions)
            with self.assertRaises(n.Blocked): n.capture(out, self.rollout(out, r))
            self.assertFalse((out / 'receipt.json').exists())

    def test_current_unknown_blocks_pass_even_if_final_is_complete(self):
        out = self.prepare(); r = self.report(out, unknowns=['correctness not verified'])
        with self.assertRaisesRegex(n.Blocked, 'unresolved current unknowns'): n.capture(out, self.rollout(out, r))
        self.assertTrue((out / 'raw-final.txt').exists()); self.assertFalse((out / 'receipt.json').exists())

    def test_original_unknown_text_and_boundary_requirements_preserved(self):
        self.prior(unknown=True)
        out = self.prepare(); prior = n.load(out / 'prior.json'); k = next(iter(prior['unknowns']))
        u = {'key': k, 'original': 'changed', 'reason': 'production boundary', 'disposition': 'boundary_retained',
             'boundary_contract': 'original Goal excludes production', 'production_gates_closed': True}
        r = self.report(out, prior_dispositions=[self.disposition(out), u])
        with self.assertRaisesRegex(n.Blocked, 'unknown text changed'): n.capture(out, self.rollout(out, r))

    def test_checkpoint_is_persisted_but_never_a_final_permit(self):
        out = self.prepare(); r = self.report(out, kind='checkpoint', ordinal=1,
                           parent_checkpoint_sha256=None, remaining_queue=['other/B.cs'])
        log = self.rollout(out, r); n.checkpoint(out, log)
        self.assertTrue((out / 'checkpoints/1.json').exists()); self.assertFalse((out / 'receipt.json').exists())
        self.assertTrue(n.checkpoint(out, log)['idempotent'])
        with self.assertRaises(n.Blocked): n.capture(out, log)

    def test_conflicting_and_skipped_checkpoints_are_rejected(self):
        out = self.prepare(); r = self.report(out, kind='checkpoint', ordinal=1,
                                          parent_checkpoint_sha256=None, remaining_queue=['other/B.cs'])
        n.checkpoint(out, self.rollout(out, r)); r['remaining_queue'] = []
        with self.assertRaisesRegex(n.Blocked, 'conflicting checkpoint'): n.checkpoint(out, self.rollout(out, r))
        r['ordinal'] = 3
        with self.assertRaisesRegex(n.Blocked, 'chain missing'): n.checkpoint(out, self.rollout(out, r), ordinal=3)

    def test_failed_report_findings_are_inherited_by_next_request(self):
        out = self.prepare(); report = self.report(out, verdict='blocked', findings=[self.finding('plan')])
        n.capture(out, self.rollout(out, report)); newer = self.prepare()
        prior = n.load(newer / 'prior.json')
        self.assertTrue(any(x['original']['id'] == 'OLD-1' for x in prior['findings'].values()))

    def test_checkpoint_restore_keeps_findings_and_refuses_live_agent(self):
        out = self.prepare(); report = self.report(out, kind='checkpoint', ordinal=1,
            parent_checkpoint_sha256=None, remaining_queue=['other/B.cs'], findings=[self.finding()])
        n.checkpoint(out, self.rollout(out, report))
        status = {'source': 'collaboration.list_agents', 'parent_thread': 'parent',
                  'observed_utc': n.datetime.now(n.timezone.utc).isoformat(),
                  'agents': [{'agent_name': '/root/reviewer', 'agent_status': 'running'}]}
        p = self.base / 'status.json'; p.write_bytes(n.encode(status))
        with self.assertRaisesRegex(n.Blocked, 'still live'): n.resume(out, '/root/recovery', p, 1)
        status['agents'][0]['agent_status'] = 'completed'; p.write_bytes(n.encode(status))
        result = n.resume(out, '/root/recovery', p, 1); newer = Path(result['request'])
        self.assertEqual(n.load(newer / 'request.json')['snapshot_hash'], n.load(out / 'request.json')['snapshot_hash'])
        self.assertTrue(n.load(newer / 'prior.json')['findings']); self.assertFalse((newer / 'permit.json').exists())
        final = self.report(newer, verdict='blocked', findings=[self.finding()])
        key = next(iter(n.load(newer / 'prior.json')['findings']))
        final['prior_dispositions'] = [dict(self.disposition(newer), key=key)]
        n.capture(newer, self.rollout(newer, final)); self.assertEqual(n.receipt(newer)[1]['verdict'], 'blocked')

    def test_missing_newest_report_blocks_old_pass(self):
        out = self.prepare(); n.capture(out, self.rollout(out)); manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json'}
        n.permit(self.root, manifest); self.assertEqual(n.gate(self.root, manifest, 'implement')['channel'], 'native-agent')
        self.prepare()
        with self.assertRaises(FileNotFoundError): n.gate(self.root, manifest, 'implement')

    def test_implementation_requires_plan_permit_and_closeout_requires_execution(self):
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json',
                    'opening_snapshot': '_workflow/b/opening.json'}
        self.write('_workflow/b/opening.json', {'batch': 'fixture'})
        self.config['manifest'] = '_workflow/b/manifest.json'; self.write(self.config['manifest'], manifest)
        self.write('_workflow/b/config.json', self.config)
        with self.assertRaises(n.Blocked): self.prepare('implementation')
        out = self.prepare(); n.capture(out, self.rollout(out)); n.permit(self.root, manifest)
        with self.assertRaisesRegex(n.Blocked, 'execution_evidence required'): self.prepare('implementation')
        with patch.object(n, 'execution_sources', return_value=[]), patch.object(n, 'implementation_proofs'):
            implementation = self.prepare('implementation'); n.capture(implementation, self.rollout(implementation))
        with self.assertRaisesRegex(n.Blocked, 'execution_evidence required'): n.gate(self.root, manifest, 'closeout')

    def test_unapproved_source_and_policy_drift_block_permission(self):
        out = self.prepare(); n.capture(out, self.rollout(out)); manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json'}
        n.permit(self.root, manifest); self.write('other/B.cs', 'unapproved')
        with self.assertRaisesRegex(n.Blocked, 'unapproved project paths'): n.gate(self.root, manifest, 'implement')

    def test_owner_policy_cannot_change_under_existing_permission(self):
        out = self.prepare(); n.capture(out, self.rollout(out)); manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json'}
        n.permit(self.root, manifest); self.write('_workflow/b/policy.json', {'source_thread': 'parent', 'request_cap_stop_disabled_for_this_goal': False})
        with self.assertRaisesRegex(n.Blocked, 'contract drift'): n.gate(self.root, manifest, 'implement')

    def test_assessment_policy_requires_high_for_unresolved_risk(self):
        self.assessment['effort'] = 'medium'
        self.write('_workflow/b/assessment.json', self.assessment)
        with self.assertRaisesRegex(n.Blocked, 'requires high'): self.prepare()

    def test_same_head_different_overlay_is_a_different_review(self):
        a = self.prepare(); self.write('other/B.cs', 'different overlay'); b = self.prepare()
        sa = n.load(a / 'snapshot.json'); sb = n.load(b / 'snapshot.json')
        self.assertEqual(sa['input']['git']['head'], sb['input']['git']['head'])
        self.assertNotEqual(sa['input']['files'], sb['input']['files'])

    def test_plan_cannot_close_new_implementation_finding(self):
        out = self.prepare(); f = self.finding(); f.update(status='closed', closure_evidence=['_workflow/b/plan.json'])
        with self.assertRaisesRegex(n.Blocked, 'new implementation finding'):
            n.capture(out, self.rollout(out, self.report(out, findings=[f])))

    def test_arbitrary_document_cannot_close_implementation(self):
        manifest = {'execution_evidence': ['_workflow/b/execution.json'], 'mutations': []}
        report = {'findings': [dict(self.finding(), status='closed', closure_evidence=['_workflow/b/plan.json'])],
                  'prior_dispositions': [], 'implementation_proofs': []}
        import execution_evidence as ee
        with patch.object(ee, 'validate_manifest'), patch.object(ee, 'validate', return_value={
            'purpose': 'current_regression', 'exit_codes': {'run': 0}, 'results': {}}):
            with self.assertRaisesRegex(n.Blocked, 'authenticated test proof'): n.implementation_proofs(self.root, manifest, report)

    def edit_rollout(self, path, change):
        events = [json.loads(s) for s in path.read_text(encoding='utf-8').splitlines()]
        change(events); path.write_text('\n'.join(json.dumps(e) for e in events), encoding='utf-8')
        return path

    def test_failed_reading_and_keyword_echo_cannot_authenticate_source(self):
        for mode in ['failure', 'echo', 'metadata', 'outside']:
            with self.subTest(mode=mode):
                out = self.prepare(); log = self.rollout(out)
                def mutate(events):
                    command = next(e['payload']['item'] for e in events if e['payload'].get('item', {}).get('type') == 'CommandExecution')
                    if mode == 'failure': command.update(exit_code=1, status='failed', stdout='', stderr='read failed')
                    elif mode == 'echo': command['command'][-1] = "echo 'Get-Content src/A.cs'"
                    elif mode == 'metadata': command.update(cwd=str(out), command=['pwsh', '-Command', "Get-Content -LiteralPath 'request.json'"])
                    else: command['cwd'] = str(self.root)
                with self.assertRaisesRegex(n.Blocked, 'successful same-turn'): n.capture(out, self.edit_rollout(log, mutate))

    def test_later_turn_completion_cannot_finish_previous_final(self):
        out = self.prepare(); log = self.rollout(out)
        def mutate(events):
            completion = events.pop(); completion['payload']['turn_id'] = 'other-turn'
            events.extend([{'type': 'event_msg', 'payload': {'type': 'task_started', 'turn_id': 'other-turn'}},
                           {'type': 'turn_context', 'timestamp': n.datetime.now(n.timezone.utc).isoformat(),
                            'payload': {'turn_id': 'other-turn', 'model': 'gpt-6.1-sol', 'effort': 'high'}}, completion])
        with self.assertRaisesRegex(n.Blocked, 'matching completed'): n.capture(out, self.edit_rollout(log, mutate))

    def test_final_event_must_match_current_turn_message(self):
        out = self.prepare(); log = self.rollout(out)
        def mutate(events):
            for e in events:
                if e['payload'].get('item', {}).get('type') == 'AgentMessage': e['payload']['turn_id'] = 'other-turn'
        with self.assertRaisesRegex(n.Blocked, 'another turn'): n.capture(out, self.edit_rollout(log, mutate))

    def test_reading_unit_started_before_request_is_rejected(self):
        out = self.prepare(); log = self.rollout(out)
        def mutate(events):
            for e in events:
                if e['type'] == 'turn_context': e['timestamp'] = '2000-01-01T00:00:00Z'
        with self.assertRaisesRegex(n.Blocked, 'predates request'): n.capture(out, self.edit_rollout(log, mutate))

    def test_same_worktree_different_staged_bytes_changes_identity(self):
        self.write('other/B.cs', 'stage one'); self.git('add', '--', 'other/B.cs'); self.write('other/B.cs', 'working')
        a = n.full_identity(self.root)
        self.write('other/B.cs', 'stage two'); self.git('add', '--', 'other/B.cs'); self.write('other/B.cs', 'working')
        b = n.full_identity(self.root)
        self.assertEqual(a['files'], b['files']); self.assertEqual(a['git']['staged_names'], b['git']['staged_names'])
        self.assertNotEqual(a['git']['staged_index_sha256'], b['git']['staged_index_sha256'])
        self.assertNotEqual(a['git']['source_diffs'], b['git']['source_diffs'])

    def test_underscore_source_is_not_assumed_to_be_output(self):
        self.write('_src/Dependency.cs', 'class Dependency {}'); out = self.prepare()
        self.assertIn('_src/Dependency.cs', n.load(out/'snapshot.json')['input']['files'])

    def test_actual_excluded_paths_and_readable_diffs_are_bound(self):
        self.write('User/private.cs', 'private'); self.write('other/B.cs', 'changed'); out = self.prepare()
        s = n.load(out/'snapshot.json')
        self.assertTrue(any(x['path'] == 'User' for x in s['input']['actual_excluded_paths']))
        self.assertTrue(s['git_files']); name = next(iter(s['git_files']))
        diff = Path(s['source']).parent/'git'/name; self.assertIn(b'changed', diff.read_bytes())
        diff.write_bytes(b'altered')
        with self.assertRaisesRegex(n.Blocked, 'readable Git diff drift'): n.verify_input(out)

    def checkpoint_units(self, out):
        saved = []
        for ordinal in range(1, 4):
            f = self.finding(); f['id'] = 'UNIT-' + str(ordinal)
            parent = n.sha((out/'checkpoints'/f'{ordinal-1}.json').read_bytes()) if ordinal > 1 else None
            report = self.report(out, kind='checkpoint', ordinal=ordinal, parent_checkpoint_sha256=parent,
                                 remaining_queue=['other/B.cs'], findings=[f], unknowns=['early unknown'] if ordinal == 1 else [])
            n.checkpoint(out, self.rollout(out, report), ordinal)
            saved.append(report)
        return saved

    def terminal_status(self):
        path = self.base/'status.json'; path.write_bytes(n.encode({'source': 'collaboration.list_agents',
            'parent_thread': 'parent', 'observed_utc': n.datetime.now(n.timezone.utc).isoformat(),
            'agents': [{'agent_name': '/root/reviewer', 'agent_status': 'completed'}]}))
        return path

    def test_three_checkpoint_units_restore_early_finding_and_unknown(self):
        out = self.prepare(); self.checkpoint_units(out)
        result = n.resume(out, '/root/recovery', self.terminal_status(), 3)
        prior = n.load(Path(result['request'])/'prior.json')
        self.assertEqual({x['original']['id'] for x in prior['findings'].values()}, {'UNIT-1', 'UNIT-2', 'UNIT-3'})
        self.assertEqual([x['original'] for x in prior['unknowns'].values()], ['early unknown'])
        self.assertEqual(len(n.load(Path(result['request'])/'resumed-chain.json')), 3)

    def test_earlier_checkpoint_drift_blocks_restore(self):
        out = self.prepare(); self.checkpoint_units(out)
        p = out/'checkpoints/1.json'; data = n.load(p); data['checkpoint']['findings'] = []; p.write_bytes(n.encode(data))
        with self.assertRaisesRegex(n.Blocked, 'chain/source drift'): n.resume(out, '/root/recovery', self.terminal_status(), 3)

    def test_later_checkpoint_cannot_downgrade_or_close_early_finding(self):
        out = self.prepare(); f = self.finding(); first = self.report(out, kind='checkpoint', ordinal=1,
            parent_checkpoint_sha256=None, remaining_queue=['other/B.cs'], findings=[f])
        n.checkpoint(out, self.rollout(out, first), 1)
        for mode in ['downgrade', 'close']:
            newer = copy.deepcopy(f)
            if mode == 'downgrade': newer['severity'] = 'suggestion'
            else: newer.update(status='closed', closure_evidence=['src/A.cs'])
            second = self.report(out, kind='checkpoint', ordinal=2,
                parent_checkpoint_sha256=n.sha((out/'checkpoints/1.json').read_bytes()),
                remaining_queue=[], findings=[newer])
            with self.assertRaises(n.Blocked): n.checkpoint(out, self.rollout(out, second), 2)
        self.assertFalse((out/'checkpoints/2.json').exists())

    def test_capture_retries_all_partial_artifacts_without_overwrite(self):
        original = n.persist_same
        for fail_name in ['native-rollout.jsonl', 'raw-final.txt', 'report.json', 'receipt.json']:
            out = self.prepare(); log = self.rollout(out)
            def interrupt(path, content, allow_prefix=False):
                if Path(path).name == fail_name:
                    Path(path).write_bytes(content[:max(1, len(content)//2)])
                    raise RuntimeError('injected publication interruption')
                return original(path, content, allow_prefix)
            with patch.object(n, 'persist_same', side_effect=interrupt):
                with self.assertRaisesRegex(RuntimeError, 'publication interruption'): n.capture(out, log)
            if fail_name == 'receipt.json':
                # Receipt itself is the final publish point: a partial receipt
                # must be completed from this source, never treated as valid.
                with self.assertRaises(ValueError): n.receipt(out)
            n.capture(out, log); self.assertEqual(n.receipt(out)[1]['verdict'], 'pass')

    def test_conflicting_capture_source_is_never_overwritten(self):
        out = self.prepare(); log = self.rollout(out); n.capture(out, log)
        before = (out/'native-rollout.jsonl').read_bytes(); conflict = self.rollout(out, self.report(out, verdict='blocked'))
        with self.assertRaisesRegex(n.Blocked, 'conflicting original capture source'): n.capture(out, conflict)
        self.assertEqual((out/'native-rollout.jsonl').read_bytes(), before)

    def legacy_fixture(self):
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json', 'opening_snapshot': '_workflow/b/opening.json'}
        self.write(manifest['opening_snapshot'], {'batch': 'fixture'})
        self.config['manifest'] = '_workflow/b/manifest.json'; self.write(self.config['manifest'], manifest)
        self.write('_workflow/b/config.json', self.config)
        source = self.write('_workflow/b/legacy-history.json', {'findings': [self.finding()]})
        self.write('_workflow/b/review-process/registration.json', {'batch': 'fixture',
            'opening': manifest['opening_snapshot'], 'opening_sha256': n.sha((self.root/manifest['opening_snapshot']).read_bytes()),
            'history_hashes': {'_workflow/b/legacy-history.json': n.sha(source.read_bytes())},
            'prior_findings': [self.finding()]})
        f = self.finding(); f.update(id='FAILED-REPORT-1', root_cause='failed report obligation')
        self.write('_workflow/b/review-process/requests/001/report.json', {'findings': [f], 'unknowns': ['legacy unknown']})
        return manifest

    def test_existing_batch_automatically_imports_registration_and_failed_report(self):
        self.legacy_fixture(); out = self.prepare(); prior = n.load(out/'prior.json')
        self.assertEqual({x['original']['id'] for x in prior['findings'].values()}, {'OLD-1', 'FAILED-REPORT-1'})
        self.assertEqual([x['original'] for x in prior['unknowns'].values()], ['legacy unknown'])
        self.assertEqual(n.load(out/'request.json')['legacy_binding']['kind'], 'adopt')

    def test_missing_legacy_history_and_changed_opening_block_dispatch(self):
        manifest = self.legacy_fixture(); (self.root/'_workflow/b/legacy-history.json').unlink()
        with self.assertRaises(FileNotFoundError): self.prepare()
        self.write('_workflow/b/legacy-history.json', {'findings': [self.finding()]})
        self.write(manifest['opening_snapshot'], {'batch': 'fixture', 'altered': True})
        with self.assertRaisesRegex(n.Blocked, 'registration/opening drift'): self.prepare()

    def test_same_original_id_different_text_is_not_deduplicated(self):
        self.legacy_fixture(); f = self.finding(); f['root_cause'] = 'different original text'
        self.write('_workflow/b/review-process/requests/002/report.json', {'findings': [f]})
        out = self.prepare(); prior = n.load(out/'prior.json')
        self.assertEqual(sum(x['original']['id'] == 'OLD-1' for x in prior['findings'].values()), 2)

    def test_new_plan_cannot_reuse_old_implementation_pass(self):
        manifest, closing, _, _ = self.authenticated_proof_fixture()
        self.write(manifest['opening_snapshot'], {'batch': 'fixture'})
        self.config['manifest'] = '_workflow/b/manifest.json'
        self.write(self.config['manifest'], manifest); self.write('_workflow/b/config.json', self.config)
        p1 = self.prepare(); n.capture(p1, self.rollout(p1)); n.permit(self.root, manifest)
        i1 = self.prepare('implementation'); n.capture(i1, self.rollout(i1))
        n.gate(self.root, manifest, 'closeout')
        p2 = self.prepare()
        finding = dict(closing['findings'][0], status='open', closure_evidence=[])
        n.capture(p2, self.rollout(p2, self.report(p2, findings=[finding])))
        n.permit(self.root, manifest)
        with self.assertRaisesRegex(n.Blocked, 'older plan/permit/manifest'):
            n.gate(self.root, manifest, 'closeout')
        i2 = self.prepare('implementation'); prior = n.load(i2/'prior.json')
        dispositions = [{'key': key, 'id': row['original']['id'], 'severity': row['original']['severity'],
            'obligation': row['original']['obligation'], 'disposition': 'resolved_with_evidence',
            'reason': 'actual current overflow assertion and target P/F/P',
            'evidence': ['subject.py', manifest['execution_evidence'][0]]}
            for key, row in prior['findings'].items()]
        report = self.report(i2, **dict(closing, prior_dispositions=dispositions))
        n.capture(i2, self.rollout(i2, report)); n.gate(self.root, manifest, 'closeout')

    def test_capture_same_source_can_finish_before_new_review_import(self):
        out = self.prepare(); log = self.rollout(out, self.report(out, verdict='blocked', findings=[self.finding('plan')]))
        original = n.persist_same
        def interrupt(path, content, allow_prefix=False):
            if Path(path).name == 'report.json': raise RuntimeError('interrupted before parsed report')
            return original(path, content, allow_prefix)
        with patch.object(n, 'persist_same', side_effect=interrupt):
            with self.assertRaises(RuntimeError): n.capture(out, log)
        newer = self.prepare(); prior = n.load(newer/'prior.json')
        self.assertTrue(any(f['original']['id'] == 'OLD-1' for f in prior['findings'].values()))

    def test_source_scope_extension_keeps_opening_and_requires_approved_paths(self):
        self.config['write_paths'].append('other/B.cs'); self.write('_workflow/b/config.json', self.config)
        plan = self.prepare(); n.capture(plan, self.rollout(plan)); request = n.load(plan/'request.json')
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json',
                    'sources': ['src/A.cs', 'other/B.cs'], 'native_scope_extension': {
                        'plan_request': request['request_id'],
                        'plan_receipt_sha256': n.sha((plan/'receipt.json').read_bytes()), 'paths': ['other/B.cs']}}
        self.assertTrue(n.validate_scope_extension(self.root, manifest, ['src/A.cs']))
        manifest['sources'].append('unknown.cs'); manifest['native_scope_extension']['paths'].append('unknown.cs')
        with self.assertRaisesRegex(n.Blocked, 'unapproved source'): n.validate_scope_extension(self.root, manifest, ['src/A.cs'])

    def test_source_scope_extension_cannot_remove_original_source(self):
        plan = self.prepare(); n.capture(plan, self.rollout(plan)); request = n.load(plan/'request.json')
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json', 'sources': [],
                    'native_scope_extension': {'plan_request': request['request_id'],
                        'plan_receipt_sha256': n.sha((plan/'receipt.json').read_bytes()), 'paths': []}}
        with self.assertRaisesRegex(n.Blocked, 'removed original'): n.validate_scope_extension(self.root, manifest, ['src/A.cs'])

    def test_approved_new_file_need_not_exist_before_plan(self):
        self.config['write_paths'].append('new/Adapter.cs'); self.write('_workflow/b/config.json', self.config)
        plan = self.prepare(); n.capture(plan, self.rollout(plan)); request = n.load(plan/'request.json')
        self.write('new/Adapter.cs', 'class Adapter {}')
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json',
            'sources': ['src/A.cs', 'new/Adapter.cs'], 'native_scope_extension': {
                'plan_request': request['request_id'], 'plan_receipt_sha256': n.sha((plan/'receipt.json').read_bytes()),
                'paths': ['new/Adapter.cs']}}
        self.assertTrue(n.validate_scope_extension(self.root, manifest, ['src/A.cs']))

    def test_actual_rg_and_serena_reads_are_recognized(self):
        out = self.prepare(); snapshot = n.load(out/'snapshot.json')
        event = {'type': 'CommandExecution', 'status': 'completed', 'exit_code': 0,
                 'cwd': snapshot['source'], 'stderr': '', 'stdout': 'src/A.cs:1:class A {}',
                 'command': ['pwsh', '-Command', "rg -n 'class' src/A.cs"]}
        self.assertTrue(n.successful_source_read(event, snapshot))
        snapshot['input']['files']['tools/mistletoe/serena_read.py'] = {'sha256': 'fixture-helper'}
        helper = str(Path(snapshot['source'])/'tools/mistletoe/serena_read.py')
        proof = {'source_root': snapshot['source'], 'tool': 'find_symbol',
                 'helper_sha256': 'fixture-helper', 'helper_path': helper, 'snapshot_hash': n.sha(n.encode(snapshot))}
        event.update(command=['python', helper], stdout=json.dumps({'isError': False,
            'content': [{'type': 'text', 'text': json.dumps([{'name_path': 'A', 'relative_path': 'src/A.cs'}])}], '_native_read': proof}))
        self.assertTrue(n.successful_source_read(event, snapshot))
        event['stdout'] = json.dumps({'isError': True, '_native_read': {'source_root': snapshot['source'],
            'tool': 'find_symbol', 'helper_sha256': 'fixture-helper'}})
        self.assertFalse(n.successful_source_read(event, snapshot))

    def test_serena_keyword_echo_and_wrong_helper_do_not_prove_read(self):
        out = self.prepare(); snapshot = n.load(out/'snapshot.json')
        snapshot['input']['files']['tools/mistletoe/serena_read.py'] = {'sha256': 'fixture-helper'}
        helper = str(Path(snapshot['source'])/'tools/mistletoe/serena_read.py')
        proof = {'source_root': snapshot['source'], 'tool': 'find_symbol', 'helper_sha256': 'fixture-helper',
                 'helper_path': helper, 'snapshot_hash': n.sha(n.encode(snapshot))}
        event = {'type': 'CommandExecution', 'status': 'completed', 'exit_code': 0,
                 'cwd': snapshot['source'], 'stderr': '',
                 'stdout': json.dumps({'isError': False, 'content': [{'type': 'text', 'text': json.dumps([{'name_path': 'A', 'relative_path': 'src/A.cs'}])}], '_native_read': proof})}
        for command in (['pwsh', '-Command', "Write-Output 'serena_read.py'"],
                        ['python', str(self.root/'tools/mistletoe/serena_read.py')]):
            event['command'] = command
            self.assertFalse(n.successful_source_read(event, snapshot))

    def test_serena_empty_symbol_results_do_not_prove_source_read(self):
        out = self.prepare(); snapshot = n.load(out/'snapshot.json')
        snapshot['input']['files']['tools/mistletoe/serena_read.py'] = {'sha256': 'fixture-helper'}
        helper = str(Path(snapshot['source'])/'tools/mistletoe/serena_read.py')
        proof = {'source_root': snapshot['source'], 'tool': 'find_symbol', 'helper_sha256': 'fixture-helper',
                 'helper_path': helper, 'snapshot_hash': n.sha(n.encode(snapshot))}
        event = {'type': 'CommandExecution', 'status': 'completed', 'exit_code': 0,
                 'cwd': snapshot['source'], 'stderr': '', 'command': ['python', helper]}
        for value in ('[]', '{}', '', 'null'):
            event['stdout'] = json.dumps({'isError': False,
                'structuredContent': {'result': value},
                'content': [{'type': 'text', 'text': value}], '_native_read': proof})
            self.assertFalse(n.successful_source_read(event, snapshot), value)

    def test_rg_after_assignment_is_a_supported_actual_read(self):
        out = self.prepare(); snapshot = n.load(out/'snapshot.json')
        event = {'type': 'CommandExecution', 'status': 'completed', 'exit_code': 0,
                 'cwd': snapshot['source'], 'stderr': '', 'stdout': 'src/A.cs:1:class A {}',
                 'command': ['pwsh', '-Command', "$pattern = 'class'; rg -n $pattern src/A.cs"]}
        self.assertTrue(n.successful_source_read(event, snapshot))

    def test_plan_permission_rejects_branch_drift_at_same_head(self):
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json'}
        plan = self.prepare(); n.capture(plan, self.rollout(plan))
        self.git('checkout', '-qb', 'other-branch')
        with self.assertRaisesRegex(n.Blocked, 'branch'):
            n.permit(self.root, manifest)

    def test_plan_permission_rejects_unapproved_index_only_drift(self):
        self.write('other/B.cs', 'class B { public const int Value = 2; }\n')
        self.git('add', '--', 'other/B.cs')
        self.write('other/B.cs', 'class B { public const int Value = 3; }\n')
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json'}
        plan = self.prepare(); n.capture(plan, self.rollout(plan))
        self.git('add', '--', 'other/B.cs')
        with self.assertRaisesRegex(n.Blocked, 'unapproved Git'):
            n.permit(self.root, manifest)

    def test_capture_source_binding_publish_is_atomic(self):
        out = self.prepare(); log = self.rollout(out)
        with patch.object(n.os, 'link', side_effect=RuntimeError('before atomic publish')):
            with self.assertRaisesRegex(RuntimeError, 'atomic publish'): n.capture(out, log)
        self.assertFalse((out/'capture-source.json').exists())
        self.assertFalse((out/'receipt.json').exists())
        n.capture(out, log); self.assertEqual(n.receipt(out)[1]['verdict'], 'pass')

    def test_checkpoint_partial_log_can_resume_same_source(self):
        out = self.prepare(); report = self.report(out, kind='checkpoint', ordinal=1,
            parent_checkpoint_sha256=None, remaining_queue=['other/B.cs'])
        log = self.rollout(out, report); original = n.persist_same
        def interrupt(path, content, allow_prefix=False):
            if Path(path).name == '1-native-rollout.jsonl':
                Path(path).write_bytes(content[:len(content)//2]); raise RuntimeError('partial checkpoint log')
            return original(path, content, allow_prefix)
        with patch.object(n, 'persist_same', side_effect=interrupt):
            with self.assertRaisesRegex(RuntimeError, 'partial checkpoint'): n.checkpoint(out, log, 1)
        self.assertFalse((out/'checkpoints/1.json').exists())
        n.checkpoint(out, log, 1); self.assertTrue((out/'checkpoints/1.json').exists())

    def test_checkpoint_metadata_publish_is_atomic(self):
        out = self.prepare(); report = self.report(out, kind='checkpoint', ordinal=1,
            parent_checkpoint_sha256=None, remaining_queue=['other/B.cs']); log = self.rollout(out, report)
        original = n.atomic_same
        def interrupt(path, content):
            if Path(path).name == '1.json': raise RuntimeError('before checkpoint complete marker')
            return original(path, content)
        with patch.object(n, 'atomic_same', side_effect=interrupt):
            with self.assertRaisesRegex(RuntimeError, 'complete marker'): n.checkpoint(out, log, 1)
        self.assertFalse((out/'checkpoints/1.json').exists()); n.checkpoint(out, log, 1)
        self.assertEqual(len(n.checkpoint_chain(out, 1, n.load(out/'request.json'), n.load(out/'snapshot.json'))), 1)

    def test_isolated_execution_subject_is_exact_explicit_source(self):
        import execution_evidence as ee
        self.write('src/subject.py', 'ORIGINAL = 1\n'); self.write('_workflow/b/subject.py', 'ORIGINAL = 1\n')
        recipe = {'input_roots': ['src'], 'input_files': [],
                  'isolated_subjects': {'_workflow/b/subject.py': 'src/subject.py'}}
        before = ee.inputs(self.root, recipe)
        self.assertEqual(before['src/subject.py'], before['_workflow/b/subject.py'])
        self.write('_workflow/b/subject.py', 'ORIGINAL = 2\n'); after = ee.inputs(self.root, recipe)
        self.assertEqual({p for p in before if before[p] != after[p]}, {'_workflow/b/subject.py'})

    def test_isolated_execution_subject_cannot_be_result_or_unbound_origin(self):
        import execution_evidence as ee
        self.write('_workflow/b/receipt.json', '{}')
        recipe = {'input_roots': ['src'], 'input_files': [],
                  'isolated_subjects': {'_workflow/b/receipt.json': 'src/A.cs'}}
        with self.assertRaisesRegex(n.Blocked, 'explicit source'): ee.inputs(self.root, recipe)
        self.write('_workflow/b/subject.py', 'x=1'); recipe['isolated_subjects'] = {'_workflow/b/subject.py': 'missing.py'}
        with self.assertRaisesRegex(n.Blocked, 'authenticated source input'): ee.inputs(self.root, recipe)

    def test_checkpoint_growth_preserves_bound_reading_unit(self):
        for interrupted in (False, True):
            with self.subTest(interrupted=interrupted):
                out = self.prepare(); report = self.report(out, kind='checkpoint', ordinal=1,
                    parent_checkpoint_sha256=None, remaining_queue=['other/B.cs'])
                log = self.rollout(out, report); original = n.persist_same
                def interrupt(path, content, allow_prefix=False):
                    if Path(path).name == '1-native-rollout.jsonl':
                        Path(path).write_bytes(content[:len(content)//2])
                        raise RuntimeError('partial checkpoint log')
                    return original(path, content, allow_prefix)
                if interrupted:
                    with patch.object(n, 'persist_same', side_effect=interrupt):
                        with self.assertRaises(RuntimeError): n.checkpoint(out, log, 1)
                else:
                    n.checkpoint(out, log, 1)
                with log.open('ab') as stream:
                    stream.write(b'{"type":"event_msg","payload":{"type":"subsequent_turn"}}\n')
                try:
                    result = n.checkpoint(out, log, 1)
                except n.Blocked as error:
                    self.fail('same-source growth must preserve checkpoint retry: ' + str(error))
                self.assertFalse(result['permission'])
                self.assertEqual(n.load(out/'checkpoints/1.json')['checkpoint'], report)

    def test_isolated_green_execution_rejects_old_subject(self):
        import execution_evidence as ee
        recipe = {'purpose': 'current_regression',
                  'isolated_subjects': {'_workflow/b/subject.py': 'src/subject.py'}}
        with self.assertRaisesRegex(n.Blocked, 'equivalent'):
            ee.validate_isolated_inputs(recipe, {'src/subject.py': 'current', '_workflow/b/subject.py': 'old'})
        ee.validate_isolated_inputs(recipe, {'src/subject.py': 'same', '_workflow/b/subject.py': 'same'})

    def test_capture_rejects_stale_isolated_subject_before_launch(self):
        import execution_evidence as ee
        import process_runner
        self.write('src/subject.py', 'VERSION = 2\n')
        self.write('_workflow/b/subject.py', 'VERSION = 1\n')
        recipe = {'purpose': 'current_regression', 'conditions': {'scope': 'fixture'},
                  'input_roots': ['src'], 'input_files': [], 'results': ['result.trx'],
                  'run_argv': [__import__('sys').executable, '{root}/_workflow/b/subject.py'],
                  'isolated_subjects': {'_workflow/b/subject.py': 'src/subject.py'}}
        self.write('_workflow/b/recipe.json', recipe)
        with patch.object(ee, 'verify_bundle', return_value='fixture'), \
             patch.object(process_runner, 'run', side_effect=AssertionError('stale source was launched')) as run:
            with self.assertRaisesRegex(n.Blocked, 'equivalent'):
                ee.capture(self.root, '_workflow/b/recipe.json', '_workflow/b/executions')
            run.assert_not_called()

    def test_isolated_execution_requires_consumed_subject(self):
        import execution_evidence as ee
        recipe = {'isolated_subjects': {'_workflow/b/subject.py': 'src/subject.py'}}
        with self.assertRaisesRegex(n.Blocked, 'consume'):
            ee.validate_isolated_command(self.root, recipe, None, ['python', str(self.root/'src/subject.py')])
        ee.validate_isolated_command(self.root, recipe, None,
                                    ['python', str(self.root/'_workflow/b/subject.py')])

    def test_isolated_mutant_requires_named_expected_failure(self):
        import execution_evidence as ee
        recipe = {'purpose': 'negative_test',
                  'isolated_subjects': {'_workflow/b/subject.py': 'src/subject.py'}}
        hashes = {'src/subject.py': 'current', '_workflow/b/subject.py': 'mutant'}
        with self.assertRaisesRegex(n.Blocked, 'expected failure'):
            ee.validate_isolated_inputs(recipe, hashes)
        recipe['expected_failure'] = {'test_id': 'named-target', 'assertion_contains': 'named assertion'}
        ee.validate_isolated_inputs(recipe, hashes)

    def test_isolated_conditions_preserve_origin_mapping(self):
        import execution_evidence as ee
        receipt = {'root': str(self.root), 'build_argv': None, 'run_argv': ['python', 'subject.py'],
                   'executables': {}, 'platform': 'fixture', 'environment_identity': {},
                   'products': {}, 'results': {'test.trx': 'hash'},
                   'recipe': {'conditions': {'scope': 'fixture'},
                              'isolated_subjects': {'_workflow/b/subject.py': 'src/first.py'}}}
        alternate = copy.deepcopy(receipt)
        alternate['recipe']['isolated_subjects']['_workflow/b/subject.py'] = 'src/second.py'
        self.assertNotEqual(ee.conditions(receipt, '_workflow/b/run/receipt.json'),
                            ee.conditions(alternate, '_workflow/b/run/receipt.json'))

    def authenticated_proof_fixture(self, include_writer_json=False):
        import execution_evidence as ee
        import workflow as w
        import uuid, difflib, sys
        source = b"CHECK_OVERFLOW = True\nEARLY_FAILURE = False\ndef multiply(a,b):\n    if CHECK_OVERFLOW and a*b > 2147483647: raise OverflowError('overflow')\n    return (a*b) & 2147483647\n"
        self.write('subject.py', source.decode())
        self.write('proof_runner.py', "import sys,unittest,uuid,xml.etree.ElementTree as E\nimport subject\nclass ProofTests(unittest.TestCase):\n    def test_product(self):\n        with self.assertRaises(OverflowError): subject.multiply(65536,65536)\n    def test_other_guard(self):\n        self.assertFalse(subject.EARLY_FAILURE, 'unrelated earlier guard')\nresult=unittest.TestResult();unittest.defaultTestLoader.loadTestsFromTestCase(ProofTests).run(result)\nns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010';E.register_namespace('',ns)\ntag=lambda v:'{'+ns+'}'+v\nroot=E.Element(tag('TestRun'));definitions=E.SubElement(root,tag('TestDefinitions'));results=E.SubElement(root,tag('Results'))\nfails={test.id():text for test,text in result.failures+result.errors}\nfor method in ('test_product','test_other_guard'):\n    name='proof_fixture.ProofTests.'+method;ident=str(uuid.uuid5(uuid.NAMESPACE_URL,name))\n    unit=E.SubElement(definitions,tag('UnitTest'),{'id':ident,'name':name})\n    E.SubElement(unit,tag('TestMethod'),{'className':'proof_fixture.ProofTests','name':method,'codeBase':__file__})\n    error=fails.get('__main__.ProofTests.'+method);row=E.SubElement(results,tag('UnitTestResult'),{'testId':ident,'testName':name,'executionId':str(uuid.uuid4()),'outcome':'Failed' if error else 'Passed'})\n    if error:\n        info=E.SubElement(E.SubElement(row,tag('Output')),tag('ErrorInfo'));E.SubElement(info,tag('Message')).text=error;E.SubElement(info,tag('StackTrace')).text=error\nsummary=E.SubElement(root,tag('ResultSummary'),{'outcome':'Failed' if fails else 'Completed'})\nE.SubElement(summary,tag('Counters'),{'total':'2','executed':'2','passed':str(2-len(fails)),'failed':str(len(fails)),'notExecuted':'0'})\nE.ElementTree(root).write(sys.argv[1],encoding='utf-8',xml_declaration=True)\nraise SystemExit(0 if result.wasSuccessful() else 1)\n")
        if include_writer_json:
            script = (self.root / 'proof_runner.py').read_text(encoding='utf-8')
            anchor = 'raise SystemExit(0 if result.wasSuccessful() else 1)'
            self.assertEqual(script.count(anchor), 1)
            writer = "from pathlib import Path\nimport json\nPath(sys.argv[1]).with_name('writer-evidence.json').write_text(json.dumps({'writer':'fixture','status':'observed'}),encoding='utf-8')\n"
            self.write('proof_runner.py', script.replace(anchor, writer + anchor))
        records = []
        recipe_base = {'input_roots': [], 'input_files': ['subject.py', 'proof_runner.py'],
            'conditions': {'scope': 'authenticated proof behavior fixture'}, 'products': [], 'results': ['result.trx'],
            'environment': {'PYTHONUTF8': '1', 'DOTNET_DbgEnableMiniDump': '0', 'COMPlus_DbgEnableMiniDump': '0'},
            'run_argv': [sys.executable, '-B', '{root}/proof_runner.py', '{out}/result.trx'], 'timeout_seconds': 60}
        if include_writer_json:
            recipe_base['results'].append('writer-evidence.json')
        try:
            for label, modified, method in [('target', source.replace(b'CHECK_OVERFLOW = True', b'CHECK_OVERFLOW = False'), 'test_product'),
                                           ('other', source.replace(b'EARLY_FAILURE = False', b'EARLY_FAILURE = True'), 'test_other_guard')]:
                folder = '_workflow/b/proof/' + label
                patch_bytes = ''.join(difflib.unified_diff(source.decode().splitlines(True), modified.decode().splitlines(True),
                    fromfile='subject.py', tofile='subject.py')).encode()
                self.write(folder+'/mutation.patch', patch_bytes.decode()); self.write(folder+'/no-build.log', 'Bound interpreted Python source.\n')
                record = {'id': label, 'source': 'subject.py', 'original_sha256': n.sha(source), 'mutant_sha256': n.sha(modified),
                    'restored_sha256': n.sha(source), 'target_test_id': str(uuid.uuid5(uuid.NAMESPACE_URL, 'proof_fixture.ProofTests.'+method)),
                    'target_name': 'proof_fixture.ProofTests.'+method, 'failure_contains': 'AssertionError', 'assertion_contains': 'AssertionError',
                    'build_log': folder+'/no-build.log', 'build_exit': 0, 'patch': folder+'/mutation.patch', 'patch_sha256': n.sha(patch_bytes)}
                for phase, data in [('baseline', source), ('mutant', modified), ('restored', source)]:
                    self.write('subject.py', data.decode()); recipe = dict(recipe_base, purpose='negative_test' if phase=='mutant' else 'current_regression')
                    if phase=='mutant': recipe['expected_failure'] = {'test_id': record['target_test_id'], 'assertion_contains': 'AssertionError'}
                    path = folder+'/'+phase+'-recipe.json'; self.write(path, recipe)
                    ref = ee.capture(self.root, path, '_workflow/b/executions'); receipt = ee.validate(self.root, ref)
                    record.update({phase+'_execution': ref, phase+'_trx': (Path(ref).parent/'result.trx').as_posix(), phase+'_exit': receipt['exit_codes']['run']})
                w.mutation_check(record, lambda path:(self.root/path).read_bytes(), lambda path:w.parse_trx((self.root/path).read_bytes()))
                records.append(record)
        finally:
            self.write('subject.py', source.decode())
        green = records[-1]['restored_execution']; full = (Path(green).parent/'result.trx').as_posix()
        manifest = {'batch': 'fixture', 'native_review': '_workflow/b/config.json', 'opening_snapshot': '_workflow/b/opening.json',
            'sources': ['subject.py', 'proof_runner.py'], 'tests': [{'path': full, 'expect_success': True}],
            'execution_evidence': [green], 'mutations': [records[0]]}
        finding = dict(self.finding(), id='F-proof', paths=['subject.py'], status='closed', closure_evidence=['subject.py', green])
        report = {'findings': [finding], 'prior_dispositions': [], 'implementation_proofs': [{'finding_id': 'F-proof',
            'execution_receipt': green, 'test_ids': [records[0]['target_test_id']], 'mutation_ids': ['target']}]}
        bad = dict(records[1], target_test_id=records[0]['target_test_id'], target_name=records[0]['target_name'])
        bad_report = copy.deepcopy(report); bad_report['implementation_proofs'][0]['mutation_ids'] = ['other']
        return manifest, report, dict(manifest, mutations=[bad]), bad_report

    def test_implementation_proofs_accepts_authenticated_mixed_results(self):
        import execution_evidence as ee
        import xml.etree.ElementTree as ET
        manifest, report, _, _ = self.authenticated_proof_fixture(include_writer_json=True)
        ref = report['implementation_proofs'][0]['execution_receipt']
        receipt = ee.validate(self.root, ref)
        self.assertEqual(set(receipt['results']), {'result.trx', 'writer-evidence.json'})
        writer = self.root / Path(ref).parent / 'writer-evidence.json'
        self.assertEqual(json.loads(writer.read_text(encoding='utf-8')), {'writer': 'fixture', 'status': 'observed'})
        try:
            n.implementation_proofs(self.root, manifest, report)
        except ET.ParseError:
            self.fail('typed_trx_mixed_receipt_parsed_as_xml')

    def test_implementation_proofs_mixed_json_tamper_is_rejected(self):
        import execution_evidence as ee
        manifest, report, _, _ = self.authenticated_proof_fixture(include_writer_json=True)
        ref = report['implementation_proofs'][0]['execution_receipt']
        ee.validate(self.root, ref)
        writer = self.root / Path(ref).parent / 'writer-evidence.json'
        original = writer.read_bytes()
        try:
            writer.write_bytes(original + b' ')
            with self.assertRaises(ee.Blocked):
                n.implementation_proofs(self.root, manifest, report)
        finally:
            writer.write_bytes(original)
        ee.validate(self.root, ref)

    def test_implementation_proofs_only_declared_full_paths_supply_ids(self):
        import execution_evidence as ee
        manifest, report, _, _ = self.authenticated_proof_fixture(include_writer_json=True)
        original_ref = report['implementation_proofs'][0]['execution_receipt']
        original = ee.validate(self.root, original_ref)
        for label, results, output in [
            ('json-only', ['writer-evidence.json'], 'result.trx'),
            ('undeclared-trx', ['extra.trx', 'writer-evidence.json'], 'extra.trx'),
            ('same-basename-other-path', ['result.trx', 'writer-evidence.json'], 'result.trx')]:
            with self.subTest(label=label):
                recipe = copy.deepcopy(original['recipe'])
                recipe['results'] = results
                recipe['run_argv'][-1] = '{out}/' + output
                recipe_path = '_workflow/b/' + label + '-recipe.json'
                self.write(recipe_path, recipe)
                extra = ee.capture(self.root, recipe_path, '_workflow/b/extra-proof-executions')
                ee.validate(self.root, extra)
                candidate = copy.deepcopy(manifest)
                candidate['execution_evidence'].append(extra)
                proof = copy.deepcopy(report)
                proof['implementation_proofs'][0]['execution_receipt'] = extra
                with self.assertRaisesRegex(n.Blocked, 'names no current passed test'):
                    n.implementation_proofs(self.root, candidate, proof)

    def test_implementation_proofs_mixed_keeps_named_proof_gates(self):
        import uuid
        manifest, report, _, _ = self.authenticated_proof_fixture(include_writer_json=True)
        for field, value, reason in [
            ('test_ids', [], 'names no current passed test'),
            ('test_ids', ['absent-test-id'], 'names no current passed test'),
            ('mutation_ids', [], 'no validated critical mutation'),
            ('test_ids', [str(uuid.uuid5(uuid.NAMESPACE_URL, 'proof_fixture.ProofTests.test_other_guard'))], 'mutation not tied to proof test')]:
            with self.subTest(field=field, value=value):
                candidate = copy.deepcopy(report)
                candidate['implementation_proofs'][0][field] = value
                with self.assertRaisesRegex(n.Blocked, reason):
                    n.implementation_proofs(self.root, manifest, candidate)

    def test_closed_implementation_proof_checks_actual_mutation_assertion(self):
        import workflow
        manifest, report, bad, bad_report = self.authenticated_proof_fixture()
        n.implementation_proofs(self.root, manifest, report)
        with self.assertRaisesRegex(workflow.EvidenceError, 'expected assertion was not killed'):
            n.implementation_proofs(self.root, bad, bad_report)

    def test_authenticated_proof_wrong_assertion_and_missing_proof_are_rejected(self):
        import workflow
        manifest, report, _, _ = self.authenticated_proof_fixture()
        wrong = copy.deepcopy(manifest); wrong['mutations'][0]['assertion_contains'] = 'not-the-failing-assertion'
        with self.assertRaisesRegex(workflow.EvidenceError, 'wrong failure'):
            n.implementation_proofs(self.root, wrong, report)
        missing = dict(report, implementation_proofs=[])
        with self.assertRaisesRegex(n.Blocked, 'lacks authenticated test proof'):
            n.implementation_proofs(self.root, manifest, missing)

    def test_implementation_freezes_mutation_legs_and_declared_contracts(self):
        import execution_evidence as ee
        record = {'recipe_path': '_workflow/b/recipe.json', 'results': {'target.trx': 'hash'},
                  'logs': {}, 'products': {}, 'inputs': {}}
        manifest = {'execution_evidence': ['_workflow/b/current/receipt.json'],
                    'mutations': [{'baseline_execution': '_workflow/b/base/receipt.json',
                                   'mutant_execution': '_workflow/b/mutant/receipt.json',
                                   'restored_execution': '_workflow/b/restored/receipt.json',
                                   'patch': '_workflow/b/mutation.patch'}],
                    'evidence': [{'path': '_workflow/b/coverage.json'}],
                    'packet': [{'path': '_workflow/b/requirements.md'}],
                    'risk_matrix': '_workflow/b/risk.json', 'opening_snapshot': '_workflow/b/opening.json'}
        with patch.object(ee, 'validate_manifest'), patch.object(ee, 'validate', return_value=record):
            refs = n.execution_sources(self.root, manifest)
        for path in ('_workflow/b/mutant/receipt.json', '_workflow/b/mutant/target.trx',
                     '_workflow/b/base/target.trx', '_workflow/b/coverage.json',
                     '_workflow/b/requirements.md', '_workflow/b/risk.json', '_workflow/b/opening.json'):
            self.assertIn(path, refs)

    def test_same_agent_final_cannot_drop_saved_checkpoint_finding(self):
        out = self.prepare()
        checkpoint = self.report(out, kind='checkpoint', ordinal=1, parent_checkpoint_sha256=None,
                                 findings=[self.finding()], remaining_queue=['other/B.cs'])
        n.checkpoint(out, self.rollout(out, checkpoint), 1)
        with self.assertRaisesRegex(n.Blocked, 'checkpoint|prior'):
            n.capture(out, self.rollout(out, self.report(out)))

    def test_same_agent_final_cannot_drop_checkpoint_unknown(self):
        out = self.prepare()
        unit = self.report(out, kind='checkpoint', ordinal=1, parent_checkpoint_sha256=None,
                           unknowns=['unresolved dependency'], remaining_queue=[])
        n.checkpoint(out, self.rollout(out, unit), 1)
        with self.assertRaisesRegex(n.Blocked, 'prior'):
            n.capture(out, self.rollout(out, self.report(out)))

    def test_same_agent_final_retains_three_units_and_receipt_binding(self):
        out = self.prepare(); units = self.checkpoint_units(out)
        prior, bindings = n.checkpoint_obligations(out, n.load(out/'request.json'), n.load(out/'snapshot.json'))
        dispositions = [{'key': key, 'id': row['original']['id'], 'severity': row['original']['severity'],
            'obligation': row['original']['obligation'], 'disposition': 'retained_open', 'reason': 'requires repair',
            'evidence': []} for key, row in prior['findings'].items()]
        dispositions += [{'key': key, 'original': row['original'], 'disposition': 'retained_open',
                         'reason': 'requires dependency evidence', 'evidence': []} for key, row in prior['unknowns'].items()]
        report = self.report(out, verdict='blocked', findings=[f for unit in units for f in unit['findings']],
                             unknowns=['early unknown'], prior_dispositions=dispositions)
        n.capture(out, self.rollout(out, report)); self.assertEqual(n.receipt(out)[1]['verdict'], 'blocked')
        self.assertEqual(n.load(out/'receipt.json')['checkpoint_hashes'], bindings)
        # Original reading-unit retries are independent of later completed units.
        first_log = out/'checkpoints/1-native-rollout.jsonl'
        self.assertTrue(n.checkpoint(out, first_log, 1)['idempotent'])
        data = n.load(out/'checkpoints/1.json'); data['checkpoint']['unknowns'] = []
        (out/'checkpoints/1.json').write_bytes(n.encode(data))
        with self.assertRaisesRegex(n.Blocked, 'chain/source drift'): n.receipt(out)

    def test_final_rejects_bound_but_incomplete_checkpoint(self):
        out = self.prepare(); base = out/'checkpoints'; base.mkdir()
        (base/'1-source.json').write_bytes(n.encode({'request_sha256': 'incomplete source'}))
        with self.assertRaisesRegex(n.Blocked, 'completed unit missing'):
            n.capture(out, self.rollout(out, self.report(out)))

    def test_checkpoint_after_final_is_rejected(self):
        out = self.prepare(); n.capture(out, self.rollout(out))
        unit = self.report(out, kind='checkpoint', ordinal=1, parent_checkpoint_sha256=None,
                           findings=[self.finding()], remaining_queue=[])
        with self.assertRaisesRegex(n.Blocked, 'final'):
            n.checkpoint(out, self.rollout(out, unit), 1)

    def test_checkpoint_derived_prior_partial_export_recovers(self):
        out = self.prepare(); unit = self.report(out, kind='checkpoint', ordinal=1,
            parent_checkpoint_sha256=None, findings=[self.finding()], remaining_queue=[])
        log = self.rollout(out, unit); original = n.persist_same
        def partial(path, content, allow_prefix=False):
            if Path(path).name == '1-prior.json':
                original(path, content[:len(content)//2], allow_prefix)
                raise RuntimeError('derived prior publication interrupted')
            return original(path, content, allow_prefix)
        with patch.object(n, 'persist_same', side_effect=partial):
            with self.assertRaises(RuntimeError): n.checkpoint(out, log, 1)
        self.assertTrue((out/'checkpoints/1.json').is_file())
        try: result = n.checkpoint(out, log, 1)
        except n.Blocked as error: self.fail('authenticated derived prior must recover: '+str(error))
        self.assertTrue(result['idempotent'])
        exported = n.load(out/'checkpoints/1-prior.json')
        self.assertTrue(any(row['original']['id']=='OLD-1' for row in exported['findings'].values()))
        path = out/'checkpoints/1-prior.json'; path.write_bytes(b'conflicting original')
        with self.assertRaises(n.Blocked): n.checkpoint(out, log, 1)
        self.assertEqual(path.read_bytes(), b'conflicting original')

    def test_actual_powershell_literal_path_array_is_supported(self):
        out = self.prepare(); snapshot = n.load(out/'snapshot.json')
        root = snapshot['source'].replace('\\','/')
        event = {'type': 'CommandExecution', 'status':'completed', 'exit_code':0,
                 'stderr':'', 'stdout':'class A {}\nclass B {}', 'cwd':root,
                 'command':['pwsh','-Command',"Get-Content -Raw -LiteralPath '"+root+"/src/A.cs','"+root+"/other/B.cs'"]}
        self.assertTrue(n.successful_source_read(event, snapshot))


if __name__ == '__main__':
    unittest.main()
