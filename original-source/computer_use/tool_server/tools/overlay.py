from __future__ import annotations

import contextlib
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Iterator


OVERLAY_DIR = Path(
    os.environ.get(
        "DOUBAO_OVERLAY_DIR",
        str(Path(tempfile.gettempdir()) / "doubao-computer-use-overlay"),
    )
)
STATE_FILE = OVERLAY_DIR / "overlay-state.json"
COMMAND_FILE = OVERLAY_DIR / "overlay-commands.jsonl"
LOG_FILE = OVERLAY_DIR / "overlay.log"

_last_command_id = 0


def _overlay_script() -> Path:
    return Path(__file__).with_name("overlay_process.py")


def _ensure_dir() -> None:
    OVERLAY_DIR.mkdir(parents=True, exist_ok=True)


def _read_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def _pid_alive(pid: int | None) -> bool:
    if not pid:
        return False
    try:
        import ctypes

        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, int(pid))
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    except Exception:
        return False


def overlay_status() -> dict[str, Any]:
    state = _read_json(STATE_FILE)
    pid = state.get("pid")
    running = _pid_alive(pid)
    state.update(
        {
            "running": running,
            "state_file": str(STATE_FILE),
            "command_file": str(COMMAND_FILE),
            "log_file": str(LOG_FILE),
        }
    )
    return state


def ensure_overlay_running() -> dict[str, Any]:
    _ensure_dir()
    state = overlay_status()
    if state.get("running"):
        return state

    creationflags = 0
    if os.name == "nt":
        creationflags = subprocess.CREATE_NO_WINDOW
    with LOG_FILE.open("ab") as log:
        subprocess.Popen(
            [
                sys.executable,
                str(_overlay_script()),
                "--state-file",
                str(STATE_FILE),
                "--command-file",
                str(COMMAND_FILE),
            ],
            cwd=str(Path(__file__).resolve().parent),
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=log,
            creationflags=creationflags,
        )

    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        state = overlay_status()
        if state.get("running") and state.get("ready"):
            return state
        time.sleep(0.05)
    return overlay_status()


def _next_command_id() -> int:
    global _last_command_id
    _last_command_id = max(_last_command_id + 1, int(time.time() * 1000))
    return _last_command_id


def send_overlay_command(
    command: dict[str, Any],
    *,
    wait_ack: bool = True,
    timeout: float = 1.5,
) -> dict[str, Any]:
    _ensure_dir()
    ensure_overlay_running()
    command_id = _next_command_id()
    payload = {
        "id": command_id,
        "created_at": time.time(),
        **command,
    }
    with COMMAND_FILE.open("a", encoding="utf-8") as f:
        f.write(json.dumps(payload, ensure_ascii=True) + "\n")

    if wait_ack:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            state = overlay_status()
            if int(state.get("last_command_id") or 0) >= command_id:
                return state
            time.sleep(0.03)
    return overlay_status()


def show_overlay(
    *,
    x: int,
    y: int,
    label: str = "",
    duration_ms: int = 1500,
    color: str = "#ff2f4f",
    radius: int = 32,
    target_x: int | None = None,
    target_y: int | None = None,
) -> dict[str, Any]:
    return send_overlay_command(
        {
            "type": "show",
            "x": int(x),
            "y": int(y),
            "target_x": target_x,
            "target_y": target_y,
            "label": label,
            "duration_ms": int(duration_ms),
            "color": color,
            "radius": int(radius),
        }
    )


def hide_overlay() -> dict[str, Any]:
    return send_overlay_command({"type": "hide"})


def stop_overlay() -> dict[str, Any]:
    return send_overlay_command({"type": "stop"}, wait_ack=False)


@contextlib.contextmanager
def overlay_capture_guard(enabled: bool = True) -> Iterator[dict[str, Any]]:
    status_before = overlay_status()
    metadata: dict[str, Any] = {
        "enabled": bool(enabled),
        "running": bool(status_before.get("running")),
        "visible_before": bool(status_before.get("visible")),
        "method": "none",
    }
    suspended = False
    if enabled and status_before.get("running") and status_before.get("visible"):
        send_overlay_command({"type": "suspend"}, wait_ack=True, timeout=1.0)
        suspended = True
        metadata["method"] = "suspend_resume"
        time.sleep(0.08)
    try:
        yield metadata
    finally:
        if suspended:
            send_overlay_command({"type": "resume"}, wait_ack=False)
            metadata["resumed"] = True


def overlay_self_test() -> dict[str, Any]:
    state = ensure_overlay_running()
    return {
        "running": bool(state.get("running")),
        "ready": bool(state.get("ready")),
        "affinity_ok": state.get("affinity_ok"),
        "hwnd": state.get("hwnd"),
        "pid": state.get("pid"),
        "status": state,
    }
