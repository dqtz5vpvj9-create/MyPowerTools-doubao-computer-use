import base64
import ctypes
import io
import time
from typing import Any

from .dpi_awareness import enable_dpi_awareness, get_process_dpi_awareness

enable_dpi_awareness()

from PIL import ImageDraw, ImageGrab

from .overlay import overlay_capture_guard


SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79

user32 = ctypes.windll.user32


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def get_cursor_position() -> dict[str, int]:
    point = POINT()
    user32.GetCursorPos(ctypes.byref(point))
    return {"x": int(point.x), "y": int(point.y)}


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


def capture_png_base64(
    *,
    scale: float = 1.0,
    include_cursor: bool = False,
    crop: tuple[int, int, int, int] | None = None,
    exclude_overlay: bool = True,
) -> dict[str, Any]:
    scale = max(0.1, min(float(scale or 1.0), 1.0))
    started_at = time.perf_counter()
    backend = "pillow-imagegrab-all-screens"
    with overlay_capture_guard(exclude_overlay) as overlay_guard:
        try:
            image = ImageGrab.grab(bbox=crop, all_screens=True)
        except OSError:
            try:
                image = ImageGrab.grab(bbox=crop)
                backend = "pillow-imagegrab-primary"
            except Exception:
                try:
                    import mss
                    from PIL import Image

                    with mss.mss() as sct:
                        if crop:
                            left, top, right, bottom = crop
                            monitor = {
                                "left": left,
                                "top": top,
                                "width": right - left,
                                "height": bottom - top,
                            }
                        else:
                            monitor = sct.monitors[0]
                        shot = sct.grab(monitor)
                        image = Image.frombytes("RGB", shot.size, shot.rgb)
                    backend = "mss"
                except Exception:
                    import pyautogui

                    if crop:
                        left, top, right, bottom = crop
                        image = pyautogui.screenshot(region=(left, top, right - left, bottom - top))
                    else:
                        image = pyautogui.screenshot()
                    backend = "pyautogui-screenshot"
    original_width, original_height = image.size
    region = (
        {
            "left": crop[0],
            "top": crop[1],
            "right": crop[2],
            "bottom": crop[3],
            "width": crop[2] - crop[0],
            "height": crop[3] - crop[1],
        }
        if crop
        else (
            virtual_screen_rect()
            if backend == "pillow-imagegrab-all-screens"
            else {
                "left": 0,
                "top": 0,
                "right": original_width,
                "bottom": original_height,
                "width": original_width,
                "height": original_height,
            }
        )
    )

    if include_cursor:
        cursor = get_cursor_position()
        draw = ImageDraw.Draw(image)
        cx = cursor["x"] - region["left"]
        cy = cursor["y"] - region["top"]
        radius = 14
        draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), outline="red", width=3)
        draw.line((cx - radius, cy, cx + radius, cy), fill="red", width=2)
        draw.line((cx, cy - radius, cx, cy + radius), fill="red", width=2)

    if scale != 1.0:
        image = image.resize(
            (max(1, int(image.width * scale)), max(1, int(image.height * scale)))
        )

    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True, compress_level=6)
    data = base64.b64encode(buffer.getvalue()).decode("ascii")
    buffer.close()
    return {
        "Screenshot": data,
        "Width": image.width,
        "Height": image.height,
        "OriginalWidth": original_width,
        "OriginalHeight": original_height,
        "Scale": scale,
        "Region": region,
        "CaptureMs": round((time.perf_counter() - started_at) * 1000, 2),
        "Backend": backend,
        "DpiAwareness": get_process_dpi_awareness(),
        "OverlayGuard": overlay_guard,
    }
