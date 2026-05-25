#!/usr/bin/env python3
"""
tiny11_hive.py — apply registry edits to an offline Windows hive file.

Used by tiny11maker.sh as a stand-in for `hivexregedit --merge`, which is
not shipped in Homebrew's hivex bottle. This script calls libhivex directly
via ctypes so it can do surgical per-value edits (hivex_node_set_value)
rather than the destructive setval-replaces-all behavior of hivexsh.

Usage:
    tiny11_hive.py <hive_path>
    # Reads a JSON list of edit ops from stdin and applies them, then commits.

Op formats (key paths use backslashes, no leading backslash, no hive prefix):
    {"action": "set_dword",  "path": "Path\\To\\Key", "name": "ValueName", "value": 1}
    {"action": "set_qword",  "path": "...", "name": "...", "value": 12345}
    {"action": "set_sz",     "path": "...", "name": "...", "value": "string"}
    {"action": "set_expand", "path": "...", "name": "...", "value": "%foo%"}
    {"action": "set_multi",  "path": "...", "name": "...", "value": ["a", "b"]}
    {"action": "set_binary", "path": "...", "name": "...", "value_hex": "deadbeef"}
    {"action": "del_key",    "path": "Path\\To\\Key"}
"""

import ctypes
import ctypes.util
import errno
import json
import os
import subprocess
import sys
from ctypes import (
    POINTER, Structure, c_char_p, c_int, c_size_t, c_ubyte, c_uint32, c_void_p,
)

# Windows registry value types
REG_NONE = 0
REG_SZ = 1
REG_EXPAND_SZ = 2
REG_BINARY = 3
REG_DWORD = 4
REG_MULTI_SZ = 7
REG_QWORD = 11

HIVEX_OPEN_VERBOSE = 1
HIVEX_OPEN_DEBUG = 2
HIVEX_OPEN_WRITE = 4
HIVEX_OPEN_UNSAFE = 8


def find_libhivex():
    name = ctypes.util.find_library("hivex")
    if name:
        return name
    candidates = [
        "/opt/homebrew/lib/libhivex.dylib",
        "/usr/local/lib/libhivex.dylib",
        "/usr/lib/libhivex.so.0",
        "/usr/lib/x86_64-linux-gnu/libhivex.so.0",
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    try:
        prefix = subprocess.check_output(
            ["brew", "--prefix", "hivex"], text=True, stderr=subprocess.DEVNULL
        ).strip()
        for ext in ("dylib", "so", "so.0"):
            cand = os.path.join(prefix, "lib", f"libhivex.{ext}")
            if os.path.exists(cand):
                return cand
    except Exception:
        pass
    return None


_lib_path = find_libhivex()
if not _lib_path:
    sys.exit("ERR: libhivex not found. brew install hivex.")

lib = ctypes.CDLL(_lib_path, use_errno=True)


class HiveSetValue(Structure):
    _fields_ = [
        ("key", c_char_p),
        ("t", c_uint32),
        ("len", c_size_t),
        ("value", POINTER(c_ubyte)),
    ]


# bind the libhivex functions we need
lib.hivex_open.argtypes = [c_char_p, c_int]
lib.hivex_open.restype = c_void_p

lib.hivex_close.argtypes = [c_void_p]
lib.hivex_close.restype = c_int

lib.hivex_commit.argtypes = [c_void_p, c_char_p, c_int]
lib.hivex_commit.restype = c_int

lib.hivex_root.argtypes = [c_void_p]
lib.hivex_root.restype = c_size_t

lib.hivex_node_get_child.argtypes = [c_void_p, c_size_t, c_char_p]
lib.hivex_node_get_child.restype = c_size_t

lib.hivex_node_add_child.argtypes = [c_void_p, c_size_t, c_char_p]
lib.hivex_node_add_child.restype = c_size_t

lib.hivex_node_delete_child.argtypes = [c_void_p, c_size_t]
lib.hivex_node_delete_child.restype = c_int

lib.hivex_node_set_value.argtypes = [c_void_p, c_size_t, POINTER(HiveSetValue), c_int]
lib.hivex_node_set_value.restype = c_int


def _check_errno(label):
    e = ctypes.get_errno()
    return f"{label}: errno={e} ({errno.errorcode.get(e, '?')}: {os.strerror(e)})"


def open_hive(path):
    h = lib.hivex_open(path.encode("utf-8"), HIVEX_OPEN_WRITE)
    if not h:
        raise RuntimeError(_check_errno(f"hivex_open({path})"))
    return h


def commit_hive(h):
    rc = lib.hivex_commit(h, None, 0)
    if rc != 0:
        raise RuntimeError(_check_errno("hivex_commit"))


def close_hive(h):
    lib.hivex_close(h)


def find_or_create_node(h, path):
    """Walk backslash-separated path from hive root, creating missing keys."""
    node = lib.hivex_root(h)
    if not node:
        raise RuntimeError(_check_errno("hivex_root"))
    if not path:
        return node
    for part in path.split("\\"):
        if not part:
            continue
        ctypes.set_errno(0)
        child = lib.hivex_node_get_child(h, node, part.encode("utf-8"))
        if not child:
            child = lib.hivex_node_add_child(h, node, part.encode("utf-8"))
            if not child:
                raise RuntimeError(_check_errno(f"add_child({part})"))
        node = child
    return node


def find_node(h, path):
    """Walk path; return 0 if any segment is missing."""
    node = lib.hivex_root(h)
    if not path:
        return node
    for part in path.split("\\"):
        if not part:
            continue
        ctypes.set_errno(0)
        node = lib.hivex_node_get_child(h, node, part.encode("utf-8"))
        if not node:
            return 0
    return node


def set_value(h, node, name, vtype, data_bytes):
    buf = (c_ubyte * len(data_bytes))(*data_bytes)
    sv = HiveSetValue(
        key=name.encode("utf-8"),
        t=vtype,
        len=len(data_bytes),
        value=buf,
    )
    rc = lib.hivex_node_set_value(h, node, ctypes.byref(sv), 0)
    if rc != 0:
        raise RuntimeError(_check_errno(f"set_value({name})"))


def apply_op(h, op):
    action = op["action"]
    path = op.get("path", "")

    if action == "del_key":
        node = find_node(h, path)
        if not node:
            return f"  skip del (not present): {path}"
        rc = lib.hivex_node_delete_child(h, node)
        if rc != 0:
            raise RuntimeError(_check_errno(f"delete_child({path})"))
        return f"  del   {path}"

    # create-as-needed for set_* ops
    node = find_or_create_node(h, path)
    name = op["name"]

    if action == "set_dword":
        data = int(op["value"]).to_bytes(4, "little", signed=False)
        set_value(h, node, name, REG_DWORD, data)
    elif action == "set_qword":
        data = int(op["value"]).to_bytes(8, "little", signed=False)
        set_value(h, node, name, REG_QWORD, data)
    elif action == "set_sz":
        data = op["value"].encode("utf-16-le") + b"\x00\x00"
        set_value(h, node, name, REG_SZ, data)
    elif action == "set_expand":
        data = op["value"].encode("utf-16-le") + b"\x00\x00"
        set_value(h, node, name, REG_EXPAND_SZ, data)
    elif action == "set_multi":
        parts = op["value"]
        data = b"".join(s.encode("utf-16-le") + b"\x00\x00" for s in parts) + b"\x00\x00"
        set_value(h, node, name, REG_MULTI_SZ, data)
    elif action == "set_binary":
        data = bytes.fromhex(op["value_hex"])
        set_value(h, node, name, REG_BINARY, data)
    else:
        raise ValueError(f"unknown action: {action}")

    return f"  set   {path}\\{name}"


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: tiny11_hive.py <hive_path>  (ops on stdin as JSON list)")

    hive_path = sys.argv[1]
    if not os.path.exists(hive_path):
        sys.exit(f"hive not found: {hive_path}")

    ops = json.load(sys.stdin)
    if not isinstance(ops, list):
        sys.exit("expected a JSON list of ops on stdin")

    print(f"hive: {hive_path}  ({len(ops)} ops)", file=sys.stderr)

    h = open_hive(hive_path)
    try:
        for op in ops:
            try:
                msg = apply_op(h, op)
                if msg:
                    print(msg, file=sys.stderr)
            except RuntimeError as e:
                print(f"  WARN: {e}  op={op}", file=sys.stderr)
        commit_hive(h)
    finally:
        close_hive(h)

    print(f"hive: {hive_path}  committed", file=sys.stderr)


if __name__ == "__main__":
    main()
