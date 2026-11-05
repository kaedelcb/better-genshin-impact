"""Read Windows integrity/attachment policy and safely probe inheritance.

The functions in this module never change an ACL, a security policy, or the
integrity label of an existing object.  The caller owns storage accounting for
the small, uniquely named inheritance probe.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import sys
import uuid


_MEDIUM_RID = 8192
_LABEL_SECURITY_INFORMATION = 0x10
_SE_FILE_OBJECT = 1
_SYSTEM_MANDATORY_LABEL_ACE_TYPE = 0x11
_INHERIT_ONLY_ACE = 0x08
_FILE_ATTRIBUTE_REPARSE_POINT = 0x400
_RPC_E_CHANGED_MODE = 0x80010106
_PROBE_BYTES = b'BetterGI integrity probe\n'


class _AclSizeInformation(ctypes.Structure):
    _fields_ = [
        ('AceCount', wintypes.DWORD),
        ('AclBytesInUse', wintypes.DWORD),
        ('AclBytesFree', wintypes.DWORD),
    ]


class _AceHeader(ctypes.Structure):
    _fields_ = [
        ('AceType', wintypes.BYTE),
        ('AceFlags', wintypes.BYTE),
        ('AceSize', wintypes.WORD),
    ]


class _SidIdentifierAuthority(ctypes.Structure):
    _fields_ = [('Value', wintypes.BYTE * 6)]


class _Guid(ctypes.Structure):
    _fields_ = [
        ('Data1', wintypes.DWORD),
        ('Data2', wintypes.WORD),
        ('Data3', wintypes.WORD),
        ('Data4', wintypes.BYTE * 8),
    ]


def _require_windows() -> None:
    if os.name != 'nt':
        raise OSError('Windows 完整性检查仅支持 Windows。')


def _win_error(message: str, code: int) -> OSError:
    detail = ctypes.FormatError(code).strip() if code else '未知错误'
    return OSError(code, f'{message}：{detail}')


def integrity_rid(path: Path) -> int:
    """Return the effective mandatory-integrity RID; an unlabeled object is Medium.

    An inherit-only ACE does not label the object itself.  Ambiguous or invalid
    effective labels fail closed rather than being guessed from localized text.
    """
    _require_windows()
    path = Path(path).absolute()
    advapi = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)

    advapi.GetNamedSecurityInfoW.argtypes = [
        wintypes.LPCWSTR, ctypes.c_int, wintypes.DWORD,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p),
    ]
    advapi.GetNamedSecurityInfoW.restype = wintypes.DWORD
    advapi.GetAclInformation.argtypes = [
        ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.c_int,
    ]
    advapi.GetAclInformation.restype = wintypes.BOOL
    advapi.GetAce.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p)]
    advapi.GetAce.restype = wintypes.BOOL
    advapi.IsValidSid.argtypes = [ctypes.c_void_p]
    advapi.IsValidSid.restype = wintypes.BOOL
    advapi.GetLengthSid.argtypes = [ctypes.c_void_p]
    advapi.GetLengthSid.restype = wintypes.DWORD
    advapi.GetSidIdentifierAuthority.argtypes = [ctypes.c_void_p]
    advapi.GetSidIdentifierAuthority.restype = ctypes.POINTER(_SidIdentifierAuthority)
    advapi.GetSidSubAuthorityCount.argtypes = [ctypes.c_void_p]
    advapi.GetSidSubAuthorityCount.restype = ctypes.POINTER(wintypes.BYTE)
    advapi.GetSidSubAuthority.argtypes = [ctypes.c_void_p, wintypes.DWORD]
    advapi.GetSidSubAuthority.restype = ctypes.POINTER(wintypes.DWORD)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p

    sacl = ctypes.c_void_p()
    descriptor = ctypes.c_void_p()
    try:
        error = advapi.GetNamedSecurityInfoW(
            str(path), _SE_FILE_OBJECT, _LABEL_SECURITY_INFORMATION,
            None, None, None, ctypes.byref(sacl), ctypes.byref(descriptor),
        )
        if error:
            raise _win_error(f'无法读取文件完整性标记 {path}', int(error))
        if not sacl.value:
            return _MEDIUM_RID

        info = _AclSizeInformation()
        if not advapi.GetAclInformation(sacl, ctypes.byref(info), ctypes.sizeof(info), 2):
            raise _win_error(f'无法读取完整性标记列表 {path}', ctypes.get_last_error())
        rids: list[int] = []
        for index in range(info.AceCount):
            ace = ctypes.c_void_p()
            if not advapi.GetAce(sacl, index, ctypes.byref(ace)) or not ace.value:
                raise _win_error(f'无法读取完整性标记 {path}', ctypes.get_last_error())
            header = _AceHeader.from_address(ace.value)
            if header.AceType != _SYSTEM_MANDATORY_LABEL_ACE_TYPE or header.AceFlags & _INHERIT_ONLY_ACE:
                continue
            # ACE_HEADER + ACCESS_MASK + the smallest SID.
            if header.AceSize < ctypes.sizeof(_AceHeader) + ctypes.sizeof(wintypes.DWORD) + 8:
                raise OSError(f'文件完整性标记格式无效：{path}')
            sid = ctypes.c_void_p(ace.value + ctypes.sizeof(_AceHeader) + ctypes.sizeof(wintypes.DWORD))
            if not advapi.IsValidSid(sid):
                raise OSError(f'文件完整性 SID 无效：{path}')
            length = int(advapi.GetLengthSid(sid))
            if length > header.AceSize - ctypes.sizeof(_AceHeader) - ctypes.sizeof(wintypes.DWORD):
                raise OSError(f'文件完整性 SID 长度无效：{path}')
            authority = advapi.GetSidIdentifierAuthority(sid)
            count = advapi.GetSidSubAuthorityCount(sid)
            if not authority or not count or bytes(authority.contents.Value) != b'\0\0\0\0\0\x10' or count.contents.value != 1:
                raise OSError(f'文件完整性 SID 不是有效的完整性标签：{path}')
            rid = advapi.GetSidSubAuthority(sid, 0)
            if not rid:
                raise OSError(f'无法读取文件完整性级别：{path}')
            rids.append(int(rid.contents.value))
        if not rids:
            return _MEDIUM_RID
        if len(rids) != 1:
            raise OSError(f'文件包含多个有效完整性标记，无法安全判断：{path}')
        return rids[0]
    finally:
        if descriptor.value:
            kernel.LocalFree(descriptor)


def _guid(value: str) -> _Guid:
    return _Guid.from_buffer_copy(uuid.UUID(value).bytes_le)


def _hresult_error(operation: str, result: int) -> OSError:
    return OSError(f'{operation}失败（HRESULT 0x{result & 0xffffffff:08X}）')


def attachment_policy(path: Path) -> int:
    """Return the unsigned CheckPolicy HRESULT without executing or prompting.

    Zero means allowed, one means a prompt, and all other values are returned
    unchanged for the caller to handle as failure/unknown.
    """
    _require_windows()
    path = Path(path).absolute()
    ole = ctypes.WinDLL('ole32', use_last_error=True)
    ole.CoInitializeEx.argtypes = [ctypes.c_void_p, wintypes.DWORD]
    ole.CoInitializeEx.restype = ctypes.c_long
    ole.CoCreateInstance.argtypes = [
        ctypes.POINTER(_Guid), ctypes.c_void_p, wintypes.DWORD,
        ctypes.POINTER(_Guid), ctypes.POINTER(ctypes.c_void_p),
    ]
    ole.CoCreateInstance.restype = ctypes.c_long
    ole.CoUninitialize.argtypes = []
    ole.CoUninitialize.restype = None

    initialized = int(ole.CoInitializeEx(None, 2)) & 0xffffffff
    if initialized not in (0, 1, _RPC_E_CHANGED_MODE):
        raise _hresult_error('初始化 Windows 安全检查', initialized)
    should_uninitialize = initialized in (0, 1)
    instance = ctypes.c_void_p()
    try:
        clsid = _guid('4125DD96-E03A-4103-8F70-E0597D803B9C')
        iid = _guid('73DB1241-1E85-4581-8E4F-A81E1D0F8C57')
        result = int(ole.CoCreateInstance(ctypes.byref(clsid), None, 1, ctypes.byref(iid), ctypes.byref(instance))) & 0xffffffff
        if result & 0x80000000 or not instance.value:
            raise _hresult_error('创建 Windows 安全检查', result)
        vtable = ctypes.cast(instance, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        set_string = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, wintypes.LPCWSTR)
        for operation, index in (('设置本地文件', 5), ('设置文件来源', 7)):
            result = int(set_string(vtable[index])(instance, str(path))) & 0xffffffff
            if result & 0x80000000:
                raise _hresult_error(operation, result)
        check = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)(vtable[9])
        return int(check(instance)) & 0xffffffff
    finally:
        if instance.value:
            vtable = ctypes.cast(instance, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            ctypes.WINFUNCTYPE(wintypes.ULONG, ctypes.c_void_p)(vtable[2])(instance)
        if should_uninitialize:
            ole.CoUninitialize()


def _reject_reparse_ancestors(path: Path) -> None:
    for item in (path, *path.parents):
        info = item.lstat()
        if item.is_symlink() or getattr(info, 'st_file_attributes', 0) & _FILE_ATTRIBUTE_REPARSE_POINT:
            raise RuntimeError(f'所选目录包含链接或重解析点，无法安全检查：{item}')


def _identity(path: Path) -> tuple[int, int]:
    info = path.lstat()
    if path.is_symlink() or getattr(info, 'st_file_attributes', 0) & _FILE_ATTRIBUTE_REPARSE_POINT:
        raise RuntimeError(f'探针路径变成了链接，已停止清理：{path}')
    if not info.st_ino:
        raise RuntimeError(f'无法确认探针文件身份，已停止清理：{path}')
    return int(info.st_dev), int(info.st_ino)


def verify_inheritance(probe_root: Path) -> None:
    """Check two new directory levels and a new file under an existing directory.

    Cleanup is deliberately non-recursive: a replaced probe or an unexpected
    child is retained and reported, never removed as if it belonged to us.
    """
    _require_windows()
    root = Path(os.path.abspath(os.fspath(probe_root)))
    if not root.is_dir():
        raise RuntimeError(f'所选目录不存在或不是文件夹：{root}')
    _reject_reparse_ancestors(root)

    probe = root / ('.bgi-integrity-probe-' + uuid.uuid4().hex)
    child = probe / 'child'
    item = child / 'probe.bin'
    created: list[tuple[Path, str, tuple[int, int] | None]] = []
    original_error = None
    try:
        probe.mkdir(exist_ok=False)
        created.append((probe, 'dir', None))
        created[-1] = (probe, 'dir', _identity(probe))
        child.mkdir(exist_ok=False)
        created.append((child, 'dir', None))
        created[-1] = (child, 'dir', _identity(child))
        with item.open('xb') as stream:
            created.append((item, 'file', None))
            created[-1] = (item, 'file', _identity(item))
            stream.write(_PROBE_BYTES)
        levels = [(p, integrity_rid(p)) for p in (probe, child, item)]
        if any(rid < _MEDIUM_RID for _, rid in levels):
            raise RuntimeError(f'所选目录的新建文件会继承低完整性标记，请选择仓库外的普通本地目录：{root}')
    except BaseException as error:
        original_error = error
        raise
    finally:
        cleanup_errors = []
        for path, kind, identity in reversed(created):
            try:
                if identity is None or _identity(path) != identity:
                    raise RuntimeError('探针身份已变化')
                if kind == 'file':
                    if path.read_bytes() != _PROBE_BYTES:
                        raise RuntimeError('探针内容已变化')
                    path.unlink()
                else:
                    path.rmdir()
            except (OSError, RuntimeError) as error:
                cleanup_errors.append(f'{path}（{error}）')
        if cleanup_errors:
            raise RuntimeError('无法安全清理本次探针，已保留：' + '；'.join(cleanup_errors)) from original_error
