#!/usr/bin/env python3
"""Read/write a libvirt serial console through `virsh console`.

This copy is intentionally repo-local so full E2E tests do not depend on a
developer-specific Codex helper path.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import select
import signal
import sys
import time
from typing import Iterable


CTRL_RIGHT_BRACKET = b"\x1d"


def decode_c_escapes(value: str) -> bytes:
    return bytes(value, "utf-8").decode("unicode_escape").encode("utf-8")


def build_command(args: argparse.Namespace) -> list[str]:
    command: list[str] = ["virsh"]
    if args.connect:
        command.extend(["-c", args.connect])
    command.extend(["console", args.domain])
    if args.devname:
        command.append(args.devname)
    if args.safe:
        command.append("--safe")
    if args.force:
        command.append("--force")
    return command


def iter_payloads(args: argparse.Namespace) -> Iterable[bytes]:
    for item in args.send or []:
        yield decode_c_escapes(item)
    for line in args.send_line or []:
        yield decode_c_escapes(line) + b"\n"
    if args.send_file:
        with open(args.send_file, "rb") as handle:
            yield handle.read()


def read_loop(fd: int, *, deadline: float, expect: re.Pattern[str] | None, quiet: bool) -> None:
    chunks: list[bytes] = []
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        readable, _, _ = select.select([fd], [], [], min(0.2, remaining))
        if not readable:
            continue
        try:
            data = os.read(fd, 8192)
        except OSError:
            break
        if not data:
            break
        chunks.append(data)
        if not quiet:
            os.write(sys.stdout.fileno(), data)
        if expect:
            text = b"".join(chunks).decode("utf-8", errors="replace")
            if expect.search(text):
                return

    if expect:
        text = b"".join(chunks).decode("utf-8", errors="replace")
        raise TimeoutError(f"console output did not match {expect.pattern!r}; output was:\n{text}")


def run_dialog(fd: int, *, steps: list[dict[str, object]], quiet: bool) -> None:
    chunks: list[bytes] = []
    search_start = 0

    for index, step in enumerate(steps, start=1):
        pattern_text = str(step["expect"])
        expect = re.compile(pattern_text)
        timeout = float(step.get("timeoutSeconds", 30))
        deadline = time.monotonic() + timeout

        while True:
            text = b"".join(chunks).decode("utf-8", errors="replace")
            match = expect.search(text, search_start)
            if match:
                search_start = match.end()
                send_line = step.get("sendLine")
                if send_line is not None:
                    os.write(fd, decode_c_escapes(str(send_line)) + b"\n")
                    time.sleep(0.1)
                break

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(
                    f"dialog step {index} did not match {pattern_text!r}; output was:\n{text}"
                )

            readable, _, _ = select.select([fd], [], [], min(0.2, remaining))
            if not readable:
                continue
            try:
                data = os.read(fd, 8192)
            except OSError as exc:
                raise TimeoutError(
                    f"dialog step {index} failed while waiting for {pattern_text!r}; output was:\n{text}"
                ) from exc
            if not data:
                raise TimeoutError(
                    f"dialog step {index} console closed before {pattern_text!r}; output was:\n{text}"
                )
            chunks.append(data)
            if not quiet:
                os.write(sys.stdout.fileno(), data)


def load_dialog_steps(args: argparse.Namespace) -> list[dict[str, object]]:
    if not args.dialog_json_env:
        return []

    raw = os.environ.get(args.dialog_json_env)
    if raw is None:
        raise ValueError(f"dialog environment variable is not set: {args.dialog_json_env}")

    value = json.loads(raw)
    if not isinstance(value, list):
        raise ValueError("dialog JSON must be a list")

    steps: list[dict[str, object]] = []
    for index, item in enumerate(value, start=1):
        if not isinstance(item, dict):
            raise ValueError(f"dialog step {index} must be an object")
        if "expect" not in item:
            raise ValueError(f"dialog step {index} is missing expect")
        steps.append(item)
    return steps


def terminate_console(fd: int, pid: int, timeout: float) -> int:
    try:
        os.write(fd, CTRL_RIGHT_BRACKET)
    except OSError:
        pass

    deadline = time.monotonic() + timeout
    status = 0
    while time.monotonic() < deadline:
        try:
            done, status = os.waitpid(pid, os.WNOHANG)
        except ChildProcessError:
            return 0
        if done:
            return os.waitstatus_to_exitcode(status)
        time.sleep(0.05)

    try:
        os.kill(pid, signal.SIGTERM)
    except ProcessLookupError:
        return 0
    try:
        _, status = os.waitpid(pid, 0)
        return os.waitstatus_to_exitcode(status)
    except ChildProcessError:
        return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Send input to and read output from `virsh console DOMAIN`.")
    parser.add_argument("domain")
    parser.add_argument("--connect")
    parser.add_argument("--devname")
    parser.add_argument("--safe", action="store_true")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--send", action="append")
    parser.add_argument("--send-line", action="append")
    parser.add_argument("--send-file")
    parser.add_argument("--read-seconds", type=float, default=5.0)
    parser.add_argument("--expect")
    parser.add_argument("--dialog-json-env")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--startup-delay", type=float, default=0.8)
    parser.add_argument("--exit-timeout", type=float, default=2.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    command = build_command(args)
    expect = re.compile(args.expect) if args.expect else None
    dialog_steps = load_dialog_steps(args)

    pid, fd = os.forkpty()
    if pid == 0:
        os.execvp(command[0], command)

    exit_code = 0
    try:
        if args.startup_delay > 0:
            time.sleep(args.startup_delay)
        if dialog_steps:
            run_dialog(fd, steps=dialog_steps, quiet=args.quiet)
        else:
            for payload in iter_payloads(args):
                if payload:
                    os.write(fd, payload)
                    time.sleep(0.1)
            read_loop(fd, deadline=time.monotonic() + max(args.read_seconds, 0.0), expect=expect, quiet=args.quiet)
    finally:
        exit_code = terminate_console(fd, pid, args.exit_timeout)
        try:
            os.close(fd)
        except OSError:
            pass
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
