"""Windows owned Job Object: contain descendants before any reviewer/test is spawned."""
from __future__ import annotations
import ctypes
from ctypes import wintypes as wt
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid
from review_support import Blocked, load, publish, require

class BasicLimits(ctypes.Structure):
    _fields_ = [('process_time', ctypes.c_int64), ('job_time', ctypes.c_int64),
                ('flags', wt.DWORD), ('minimum', ctypes.c_size_t), ('maximum', ctypes.c_size_t),
                ('active_limit', wt.DWORD), ('affinity', ctypes.c_size_t),
                ('priority', wt.DWORD), ('scheduling', wt.DWORD)]

class IO(ctypes.Structure):
    _fields_ = [(n, ctypes.c_uint64) for n in ('read_ops', 'write_ops', 'other_ops', 'read_bytes', 'write_bytes', 'other_bytes')]

class Limits(ctypes.Structure):
    _fields_ = [('basic', BasicLimits), ('io', IO), ('process_memory', ctypes.c_size_t),
                ('job_memory', ctypes.c_size_t), ('peak_process', ctypes.c_size_t), ('peak_job', ctypes.c_size_t)]

class Accounting(ctypes.Structure):
    _fields_ = [(n, ctypes.c_int64) for n in ('user', 'kernel', 'period_user', 'period_kernel')] + [
        ('faults', wt.DWORD), ('total', wt.DWORD), ('active', wt.DWORD), ('terminated', wt.DWORD)]

def kernel():
    require(os.name == 'nt', 'recorded execution requires Windows Job Object containment on this release')
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    definitions = {
        'CreateJobObjectW': ([ctypes.c_void_p, wt.LPCWSTR], wt.HANDLE),
        'OpenJobObjectW': ([wt.DWORD, wt.BOOL, wt.LPCWSTR], wt.HANDLE),
        'SetInformationJobObject': ([wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD], wt.BOOL),
        'QueryInformationJobObject': ([wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p], wt.BOOL),
        'AssignProcessToJobObject': ([wt.HANDLE, wt.HANDLE], wt.BOOL),
        'GetCurrentProcess': ([], wt.HANDLE),
        'TerminateJobObject': ([wt.HANDLE, wt.UINT], wt.BOOL),
        'CloseHandle': ([wt.HANDLE], wt.BOOL),
        'GetProcessTimes': ([wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p], wt.BOOL),
    }
    for name, (args, result) in definitions.items():
        getattr(k, name).argtypes = args; getattr(k, name).restype = result
    return k

def checked(result):
    if not result:
        raise ctypes.WinError(ctypes.get_last_error())
    return result

class Job:
    def __init__(self):
        self.k = kernel(); self.name = 'Local\\Mistletoe-' + uuid.uuid4().hex
        self.handle = checked(self.k.CreateJobObjectW(None, self.name))
        limits = Limits(); limits.basic.flags = 0x2000  # KILL_ON_JOB_CLOSE, no breakaway.
        checked(self.k.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)))

    def active(self):
        info = Accounting()
        checked(self.k.QueryInformationJobObject(self.handle, 1, ctypes.byref(info), ctypes.sizeof(info), None))
        return info.active

    def terminate(self):
        checked(self.k.TerminateJobObject(self.handle, 1))
        deadline = time.monotonic() + 5
        while self.active() and time.monotonic() < deadline:
            time.sleep(.02)
        require(self.active() == 0, 'owned process tree did not terminate')

    def close(self):
        if self.handle:
            checked(self.k.CloseHandle(self.handle)); self.handle = None

def run(argv, *, cwd, env, directory, recovery_directory, phase, timeout, input_bytes=b''):
    """Return recorded command exit. Any cancellation/uncertainty preserves recovery lock."""
    directory, recovery_directory = Path(directory), Path(recovery_directory)
    job = Job()
    request_file = directory / (phase + '-process-request.json')
    stdin_file = directory / (phase + '-stdin.txt')
    stdout_file, stderr_file = directory / (phase + '-stdout.log'), directory / (phase + '-stderr.log')
    outcome = directory / (phase + '-process-result.json')
    inflight = recovery_directory / 'inflight.json'
    proc = None
    try:
        stdin_file.write_bytes(input_bytes)
        publish(request_file, {'argv': argv, 'cwd': str(cwd), 'job': job.name,
                'stdin': str(stdin_file), 'stdout': str(stdout_file), 'stderr': str(stderr_file), 'outcome': str(outcome)})
        publish(inflight, {'request': str(request_file), 'job': job.name, 'state': 'starting'})
        proc = subprocess.Popen([sys.executable, '-B', str(Path(__file__).resolve()), '--child', str(request_file)],
                                env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                creationflags=0x08000000)  # CREATE_NO_WINDOW, helper joins job before spawning.
        times = [ctypes.c_uint64() for _ in range(4)]
        checked(job.k.GetProcessTimes(wt.HANDLE(int(proc._handle)), *(ctypes.byref(t) for t in times)))
        publish(directory / (phase + '-process-identity.json'), {'pid': proc.pid, 'creation_filetime': times[0].value, 'job': job.name})
        helper_exit = proc.wait(timeout=timeout)
        require(helper_exit == 0 and outcome.exists(), 'runner did not publish a normal terminal result')
        require(job.active() == 0, 'owned descendants still active after command completion')
        result = load(outcome)
        publish(directory / (phase + '-tree-terminal.json'), {'job': job.name, 'active_processes': 0, 'helper_exit': helper_exit})
        inflight.unlink()
        return result['exit_code'], stdout_file, stderr_file
    except BaseException:
        # Even if persisting recovery fails, inflight keeps lock() fail-closed.
        try:
            publish(recovery_directory / 'recovery-required.json', {'request': str(request_file),
                    'job': job.name, 'reason': 'cancel/error/unknown terminal; original request remains counted'})
        finally:
            try:
                if proc is not None and proc.poll() is None:
                    proc.kill()  # Stop our helper even if cancellation raced its Job assignment.
                    proc.wait(timeout=5)
                job.terminate()
                if proc is not None:
                    proc.wait(timeout=5)
                publish(directory / (phase + '-cleanup.json'), {'job': job.name, 'active_processes': 0})
            except BaseException:
                pass  # Never infer cleanup success; retained inflight/lock require inspection.
        raise
    finally:
        job.close()

def child(request_file):
    request = load(request_file); k = kernel()
    job = checked(k.OpenJobObjectW(0x1F003F, False, request['job']))
    # This helper has not spawned anything yet. Descendants inherit this non-breakaway job.
    checked(k.AssignProcessToJobObject(job, k.GetCurrentProcess()))
    try:
        with open(request['stdout'], 'xb') as stdout, open(request['stderr'], 'xb') as stderr:
            result = subprocess.run(request['argv'], cwd=request['cwd'], input=Path(request['stdin']).read_bytes(),
                                    stdout=stdout, stderr=stderr, shell=False)
        publish(request['outcome'], {'exit_code': result.returncode})
    finally:
        checked(k.CloseHandle(job))

if __name__ == '__main__':
    child(sys.argv[2])
