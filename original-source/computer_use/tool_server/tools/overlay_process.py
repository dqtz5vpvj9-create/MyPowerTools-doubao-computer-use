from __future__ import annotations

import argparse
import ctypes
import json
import os
import time
import tkinter as tk
from pathlib import Path
from typing import Any


GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_NOACTIVATE = 0x08000000
HWND_TOPMOST = -1
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOACTIVATE = 0x0010
SWP_SHOWWINDOW = 0x0040
WDA_EXCLUDEFROMCAPTURE = 0x00000011
WDA_MONITOR = 0x00000001
SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79

TRANSPARENT_COLOR = "#010203"

user32 = ctypes.WinDLL("user32", use_last_error=True)


def virtual_screen_rect() -> dict[str, int]:
    left = int(user32.GetSystemMetrics(SM_XVIRTUALSCREEN))
    top = int(user32.GetSystemMetrics(SM_YVIRTUALSCREEN))
    width = int(user32.GetSystemMetrics(SM_CXVIRTUALSCREEN))
    height = int(user32.GetSystemMetrics(SM_CYVIRTUALSCREEN))
    return {
        "left": left,
        "top": top,
        "right": left + width,
        "bottom": top + height,
        "width": width,
        "height": height,
    }


def apply_window_protection(hwnd: int) -> dict[str, Any]:
    ctypes.set_last_error(0)
    affinity_ok = bool(user32.SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
    affinity_last_error = ctypes.get_last_error()
    affinity_mode = "exclude_from_capture"
    if not affinity_ok:
        ctypes.set_last_error(0)
        affinity_ok = bool(user32.SetWindowDisplayAffinity(hwnd, WDA_MONITOR))
        affinity_last_error = ctypes.get_last_error()
        affinity_mode = "monitor"

    ex_style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
    ex_style |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
    user32.SetWindowLongW(hwnd, GWL_EXSTYLE, ex_style)
    user32.SetWindowPos(
        hwnd,
        HWND_TOPMOST,
        0,
        0,
        0,
        0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
    )
    return {
        "affinity_ok": affinity_ok,
        "affinity_mode": affinity_mode,
        "affinity_last_error": int(affinity_last_error),
        "ex_style": int(user32.GetWindowLongW(hwnd, GWL_EXSTYLE)),
    }


def position_window(hwnd: int, region: dict[str, int], *, show: bool = False) -> None:
    flags = SWP_NOACTIVATE
    if show:
        flags |= SWP_SHOWWINDOW
    user32.SetWindowPos(
        hwnd,
        HWND_TOPMOST,
        int(region["left"]),
        int(region["top"]),
        int(region["width"]),
        int(region["height"]),
        flags,
    )


class OverlayApp:
    def __init__(self, *, state_file: Path, command_file: Path):
        self.state_file = state_file
        self.command_file = command_file
        self.command_offset = 0
        self.last_command_id = 0
        self.ready = False
        self.visible = False
        self.suspended = False
        self.restore_after_suspend = False
        self.last_marker: dict[str, Any] | None = None
        self.hide_job: str | None = None
        self.region = virtual_screen_rect()

        self.root = tk.Tk()
        self.root.withdraw()
        self.root.overrideredirect(True)
        self.root.configure(bg=TRANSPARENT_COLOR)
        self.root.attributes("-topmost", True)
        self.root.attributes("-transparentcolor", TRANSPARENT_COLOR)
        geometry = (
            f"{self.region['width']}x{self.region['height']}"
            f"{self.region['left']:+d}{self.region['top']:+d}"
        )
        self.root.geometry(geometry)

        self.canvas = tk.Canvas(
            self.root,
            bg=TRANSPARENT_COLOR,
            highlightthickness=0,
            bd=0,
        )
        self.canvas.pack(fill="both", expand=True)
        self.root.update_idletasks()
        self.hwnd = int(self.root.winfo_id())
        self.protection = apply_window_protection(self.hwnd)
        position_window(self.hwnd, self.region, show=False)
        self.ready = True
        self.write_state()

    def write_state(self) -> None:
        payload = {
            "pid": os.getpid(),
            "hwnd": self.hwnd,
            "ready": self.ready,
            "visible": self.visible,
            "suspended": self.suspended,
            "last_command_id": self.last_command_id,
            "region": self.region,
            "last_marker": self.last_marker,
            "updated_at": time.time(),
            **self.protection,
        }
        tmp = self.state_file.with_suffix(".tmp")
        tmp.write_text(json.dumps(payload, ensure_ascii=True), encoding="utf-8")
        tmp.replace(self.state_file)

    def poll_commands(self) -> None:
        try:
            if self.command_file.exists():
                with self.command_file.open("r", encoding="utf-8") as f:
                    f.seek(self.command_offset)
                    lines = f.readlines()
                    self.command_offset = f.tell()
                for line in lines:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        self.handle_command(json.loads(line))
                    except Exception as e:
                        self.last_marker = {"error": str(e)}
                        self.write_state()
        finally:
            self.root.after(80, self.poll_commands)

    def handle_command(self, command: dict[str, Any]) -> None:
        self.last_command_id = int(command.get("id") or self.last_command_id)
        command_type = command.get("type")
        if command_type == "show":
            self.show(command)
        elif command_type == "hide":
            self.hide()
        elif command_type == "suspend":
            self.suspend()
        elif command_type == "resume":
            self.resume()
        elif command_type == "stop":
            self.write_state()
            self.root.destroy()
            return
        elif command_type == "status":
            self.write_state()
        self.write_state()

    def show(self, command: dict[str, Any]) -> None:
        self.canvas.delete("all")
        color = str(command.get("color") or "#ff2f4f")
        radius = int(command.get("radius") or 32)
        x = int(command.get("x") or 0) - self.region["left"]
        y = int(command.get("y") or 0) - self.region["top"]
        target_x = command.get("target_x")
        target_y = command.get("target_y")
        if target_x is not None and target_y is not None:
            tx = int(target_x) - self.region["left"]
            ty = int(target_y) - self.region["top"]
            self.canvas.create_line(x, y, tx, ty, fill=color, width=4, arrow=tk.LAST)
            self.canvas.create_oval(tx - radius, ty - radius, tx + radius, ty + radius, outline=color, width=4)
        self.canvas.create_oval(x - radius, y - radius, x + radius, y + radius, outline=color, width=4)
        self.canvas.create_line(x - radius, y, x + radius, y, fill=color, width=2)
        self.canvas.create_line(x, y - radius, x, y + radius, fill=color, width=2)
        label = str(command.get("label") or "").strip()
        if label:
            label = label[:96]
            text_x = min(max(x + radius + 12, 8), self.region["width"] - 340)
            text_y = min(max(y - radius, 8), self.region["height"] - 48)
            text_id = self.canvas.create_text(
                text_x + 10,
                text_y + 10,
                anchor="nw",
                text=label,
                fill="#ffffff",
                font=("Segoe UI", 11, "bold"),
                width=310,
            )
            bbox = self.canvas.bbox(text_id) or (text_x, text_y, text_x + 320, text_y + 40)
            rect = self.canvas.create_rectangle(
                bbox[0] - 8,
                bbox[1] - 6,
                bbox[2] + 8,
                bbox[3] + 6,
                fill="#111827",
                outline=color,
                width=2,
            )
            self.canvas.tag_lower(rect, text_id)

        self.last_marker = command
        self.suspended = False
        self.restore_after_suspend = False
        self.root.deiconify()
        self.root.lift()
        self.root.update_idletasks()
        self.protection = apply_window_protection(self.hwnd)
        position_window(self.hwnd, self.region, show=True)
        self.visible = True
        if self.hide_job:
            self.root.after_cancel(self.hide_job)
        duration = int(command.get("duration_ms") or 0)
        if duration > 0:
            self.hide_job = self.root.after(duration, self.hide)

    def hide(self) -> None:
        if self.hide_job:
            self.root.after_cancel(self.hide_job)
            self.hide_job = None
        self.canvas.delete("all")
        self.root.withdraw()
        self.visible = False
        self.suspended = False
        self.restore_after_suspend = False
        self.write_state()

    def suspend(self) -> None:
        self.restore_after_suspend = self.visible
        if self.visible:
            self.root.withdraw()
        self.visible = False
        self.suspended = True

    def resume(self) -> None:
        if self.restore_after_suspend:
            self.root.deiconify()
            self.root.lift()
            self.root.update_idletasks()
            self.protection = apply_window_protection(self.hwnd)
            position_window(self.hwnd, self.region, show=True)
            self.visible = True
        self.suspended = False
        self.restore_after_suspend = False

    def run(self) -> None:
        self.root.after(80, self.poll_commands)
        self.root.mainloop()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--state-file", required=True)
    parser.add_argument("--command-file", required=True)
    args = parser.parse_args()
    state_file = Path(args.state_file)
    command_file = Path(args.command_file)
    state_file.parent.mkdir(parents=True, exist_ok=True)
    command_file.parent.mkdir(parents=True, exist_ok=True)
    command_file.touch(exist_ok=True)
    OverlayApp(state_file=state_file, command_file=command_file).run()


if __name__ == "__main__":
    main()
