"""限定拓扑修复的纯函数/防覆盖测试，不连接真实服务器。"""
import ast
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch, MagicMock

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('route_https_repair', HERE / 'repair_route_https.py')
repair = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(repair)


class RouteRepairTests(unittest.TestCase):
    def test_missing_dotenv_value_is_distinct_from_empty(self):
        self.assertIsNone(repair.dotenv_value('OTHER=unchanged\n', 'ROUTE_UPLOAD_TOKEN'))
        self.assertEqual(repair.dotenv_value('ROUTE_UPLOAD_TOKEN=\n', 'ROUTE_UPLOAD_TOKEN'), '')

    def test_empty_value_does_not_consume_next_line(self):
        text = 'ROUTE_UPLOAD_TOKEN=\nHOEING_REQUIRED_CLIENT_VERSION=0.67.0\n'
        self.assertEqual(repair.dotenv_value(text, 'ROUTE_UPLOAD_TOKEN'), '')

    def test_crlf_dotenv_is_read_without_rewriting(self):
        self.assertEqual(repair.dotenv_value('ROUTE_UPLOAD_TOKEN=abc\r\nOTHER=def\r\n',
                                             'ROUTE_UPLOAD_TOKEN'), 'abc')

    @patch.object(repair.subprocess, 'run')
    def test_windows_transport_keeps_remote_command_constant(self, command):
        command.return_value.returncode = 0
        with patch.object(repair.sys, 'argv', ['repair_route_https.py', '--apply']):
            self.assertEqual(repair.main(), 0)
        self.assertEqual(command.call_args.args[0][-1], 'python3 -I -B -')
        wrapper = command.call_args.kwargs['input'].decode('utf-8')
        ast.parse(wrapper)
        self.assertIn('scope["apply"]', wrapper)
        self.assertNotIn(' -c ', command.call_args.args[0][-1])

    def test_quoted_token_and_exact_version(self):
        self.assertEqual(repair.dotenv_value('export ROUTE_UPLOAD_TOKEN="abc-123_xyz"\n',
                                            'ROUTE_UPLOAD_TOKEN'), 'abc-123_xyz')
        self.assertEqual(repair.dotenv_value('HOEING_REQUIRED_CLIENT_VERSION=0.67.0+LCB.20261117\n',
                                            'HOEING_REQUIRED_CLIENT_VERSION'), '0.67.0+LCB.20261117')

    def test_duplicate_and_ambiguous_values_fail(self):
        with self.assertRaises(RuntimeError):
            repair.dotenv_value('ROUTE_UPLOAD_TOKEN=a\nROUTE_UPLOAD_TOKEN=b\n', 'ROUTE_UPLOAD_TOKEN')
        with self.assertRaises(RuntimeError):
            repair.dotenv_value('ROUTE_UPLOAD_TOKEN=a b\n', 'ROUTE_UPLOAD_TOKEN')

    @patch.object(repair, 'run')
    def test_changed_proxy_file_is_never_deleted(self, command):
        command.return_value = b'other writer data'
        with self.assertRaisesRegex(RuntimeError, 'changed'):
            repair.nginx_remove_owned({'http.conf': 'our content'})
        self.assertEqual(command.call_count, 1)
        self.assertNotIn('rm', command.call_args.args[0])

    @patch.object(repair, 'run')
    def test_owned_proxy_removal_then_reload(self, command):
        command.side_effect = [b'our content', b'', b'', b'']
        repair.nginx_remove_owned({'http.conf': 'our content'})
        calls = [call.args[0] for call in command.call_args_list]
        self.assertIn('rm', calls[1])
        self.assertEqual(calls[-2][-2:], ['nginx', '-t'])
        self.assertEqual(calls[-1][-3:], ['nginx', '-s', 'reload'])

    @patch.object(repair.socket, 'create_connection')
    @patch.object(repair, 'run')
    def test_proxy_source_is_observed_not_assumed(self, command, connect):
        connection = MagicMock()
        connection.recv.return_value = b'HTTP/1.1 200 OK\r\n'
        connect.return_value.__enter__.return_value = connection
        command.side_effect = [b'  sl local_address rem_address st\n',
            b'  sl local_address rem_address st\n  0: 020017AC:0050 010017AC:80F6 01\n']
        self.assertEqual(repair.actual_proxy_address(), '172.23.0.1')

    @patch.object(repair.socket, 'create_connection')
    @patch.object(repair, 'run')
    def test_ipv4_mapped_tcp6_proxy_source(self, command, connect):
        connection = MagicMock()
        connection.recv.return_value = b'HTTP/1.1 200 OK\r\n'
        connect.return_value.__enter__.return_value = connection
        command.side_effect = [b'  sl local_address rem_address st\n',
            b'  sl local_address rem_address st\n'
            b'  0: 0000000000000000FFFF0000020017AC:0050 '
            b'0000000000000000FFFF0000010017AC:80F6 01\n']
        self.assertEqual(repair.actual_proxy_address(), '172.23.0.1')

    @patch.object(repair, 'run')
    def test_missing_uncommitted_proxy_file_does_not_block_rollback(self, command):
        command.side_effect = [b'missing', b'', b'']
        repair.nginx_remove_owned({'http.conf': 'our content'})
        self.assertFalse(any('rm' in call.args[0] for call in command.call_args_list))

    @patch.object(repair, 'run')
    def test_proxy_write_is_atomic_and_never_overwrites(self, command):
        repair.nginx_write('http.conf', 'our content')
        shell = command.call_args.args[0][6]
        self.assertIn('mktemp', shell)
        self.assertIn('ln -- "$tmp" "$1"', shell)
        self.assertNotIn('ln -f', shell)
        self.assertEqual(command.call_args.kwargs['data'], b'our content')
        with self.assertRaises(ValueError):
            repair.nginx_write('http.conf', 'our content', replace=True)

    @patch.object(repair.socket, 'create_connection')
    @patch.object(repair, 'run')
    def test_unknown_proxy_source_blocks_activation(self, command, connect):
        connect.return_value.__enter__.return_value.recv.return_value = b'HTTP/1.1 200 OK\r\n'
        command.return_value = b'  sl local_address rem_address st\n'
        with self.assertRaisesRegex(RuntimeError, 'actual direct proxy'):
            repair.actual_proxy_address()

    @patch.object(repair, 'admin_client')
    def test_member_task_blocks_restart_even_without_game_room(self, authenticate):
        response = io.BytesIO(json.dumps({'startedRoomCount': 0, 'controlRooms': [
            {'autoHoeingRunning': False, 'members': [{'autoHoeingRunning': True}]}],
            'connectedControlMemberCount': 1}).encode())
        authenticate.return_value.open.return_value.__enter__.return_value = response
        with self.assertRaisesRegex(RuntimeError, 'task is running'):
            repair.assert_idle('test-only-placeholder')

    @patch.object(repair, 'admin_client')
    def test_missing_task_marker_is_not_idle(self, authenticate):
        response = io.BytesIO(json.dumps({'startedRoomCount': 0, 'controlRooms': [
            {'autoHoeingRunning': False, 'members': [{}]}],
            'connectedControlMemberCount': 1}).encode())
        authenticate.return_value.open.return_value.__enter__.return_value = response
        with self.assertRaises(KeyError):
            repair.assert_idle('test-only-placeholder')

    def test_only_new_site_uses_local_proxy_parameters(self):
        proxy = (HERE / 'route-proxy.conf.example').read_text(encoding='utf-8')
        location = (HERE / 'route-proxy-location.conf.example').read_text(encoding='utf-8')
        self.assertIn('server 127.0.0.1:18080;', proxy)
        self.assertIn('listen 8080;', proxy)
        self.assertIn('server_name www.autobgi.cn;', proxy)
        self.assertIn('proxy_set_header X-Forwarded-For $remote_addr;', location)
        self.assertIn('proxy_set_header X-Forwarded-Proto $scheme;', location)
        self.assertNotIn('$proxy_add_x_forwarded_for', location)
        self.assertIn('proxy_set_header Upgrade $http_upgrade;', location)
        self.assertNotIn('/etc/nginx/conf.d/include/proxy.conf', location)


if __name__ == '__main__':
    unittest.main()
