# Copyright (c) 2025 Bytedance Ltd. and/or its affiliates
# Licensed under the 【火山方舟】原型应用软件自用许可协议
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at 
#     https://www.volcengine.com/docs/82379/1433703
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

import time

from .dpi_awareness import enable_dpi_awareness

enable_dpi_awareness()

from PIL import ImageChops, ImageGrab
from fastapi import HTTPException
from .base import BaseError, wrap_pyautogui_async, camel_to_snake
from .password import change_password
from .computer import *
from .overlay import hide_overlay, overlay_self_test, overlay_status, show_overlay
from .windows_native import capture_png_base64, get_cursor_position

MOUSE_OPERATE_INTERVAL = 0.2
SCROLL_SCALE = 100
SHORTCUT_KEY_ALIASES = {
    "windows": "win",
    "command": "win",
    "option": "alt",
}


def _screen_signature():
    try:
        image = ImageGrab.grab(all_screens=True)
    except OSError:
        image = ImageGrab.grab()
    image = image.resize((160, 90)).convert("L")
    return image


def _screen_diff_ratio(a, b) -> float:
    diff = ImageChops.difference(a, b)
    histogram = diff.histogram()
    total = sum(value * count for value, count in enumerate(histogram))
    return total / (255 * a.width * a.height)


class PyAutoGUIComputerTool(IComputerTool):
    def __init__(self):
        super().__init__()
        self.logger = logging.getLogger(__name__)

    @wrap_pyautogui_async
    def move_mouse(self, r: MoveMouseRequest):
        return pyautogui.moveTo(r.x, r.y)

    @wrap_pyautogui_async
    def click_mouse(self, r: ClickMouseRequest):
        button = r.button
        press = r.press
        release = r.release
        x, y = r.x, r.y
        if button is None or button == "":
            button = "left"
        button = camel_to_snake(button)
        if button not in ["left", "right", "middle", "double_left"]:
            raise HTTPException(status_code=400, detail=f"Invalid button")
        if press and not release:
            return self.press_mouse(PressMouseRequest(x=x, y=y, button=button))
        elif release and not press:
            return self.release_mouse(ReleaseMouseRequest(x=x, y=y, button=button))
        else:
            clicks = 1
            if button == "double_click" or button == "double_left":
                button = "left"
                clicks = 2
            pyautogui.moveTo(x, y)
            time.sleep(0.1)
            result = pyautogui.click(clicks=clicks, button=button)
            time.sleep(0.05)
            return result

    @wrap_pyautogui_async
    def press_mouse(self, r: PressMouseRequest):
        return pyautogui.mouseDown(r.x, r.y, button=r.button.upper())

    @wrap_pyautogui_async
    def release_mouse(self, r: ReleaseMouseRequest):
        return pyautogui.mouseUp(r.x, r.y, button=r.button.upper())

    async def drag_mouse(self, r: DragMouseRequest):
        drag_path = gen_path(r.source_x, r.source_y, r.target_x, r.target_y)
        if drag_path is None or len(drag_path) < 2:
            raise BaseError(f"drag_path is required for drag")
        for loc in drag_path:
            if len(loc) != 2:
                raise BaseError(f"drag_path location must be x, y")

        await self.press_mouse(PressMouseRequest(x=drag_path[0][0], y=drag_path[0][1], button="left"))
        for loc in drag_path:
            time.sleep(MOUSE_OPERATE_INTERVAL)
            await self.move_mouse(MoveMouseRequest(x=loc[0], y=loc[1]))
        time.sleep(MOUSE_OPERATE_INTERVAL)
        return await self.release_mouse(ReleaseMouseRequest(x=drag_path[-1][0], y=drag_path[-1][1], button="left"))

    @wrap_pyautogui_async
    def scroll(self, r: ScrollRequest):
        scroll_direction = r.scroll_direction
        scroll_amount = r.scroll_amount
        x, y = r.x, r.y
        scroll_amount = int(scroll_amount) * SCROLL_SCALE
        self.logger.info("scroll in windows, amount: %s, direction: %s", scroll_amount, scroll_direction)
        if scroll_direction == "up":
            scroll = pyautogui.vscroll
        elif scroll_direction == "down":
            scroll_amount = -scroll_amount
            scroll = pyautogui.vscroll
        elif scroll_direction == "left":
            scroll = pyautogui.hscroll
        elif scroll_direction == "right":
            scroll_amount = -scroll_amount
            scroll = pyautogui.hscroll
        else:
            raise ValueError(f"Invalid scroll direction: {scroll_direction}")
        return scroll(scroll_amount, x, y)

    @wrap_pyautogui_async
    def press_key(self, r: PressKeyRequest):
        keys = [x for x in r.key.split(' ') if x]
        if len(keys) == 0:
            return pyautogui.press(r.key)
        else:
            return pyautogui.hotkey(*keys)

    @wrap_pyautogui_async
    def type_text(self, r: TypeTextRequest):
        return paste(r.text)

    @wrap_pyautogui_async
    def wait(self, r: WaitRequest):
        duration = r.duration
        if isinstance(r.duration, str):
            duration = int(r.duration)
        return time.sleep(duration / 1000)

    async def take_screenshot(self, r: TakeScreenshotRequest):
        crop = None
        if None not in (r.left, r.top, r.right, r.bottom):
            crop = (r.left, r.top, r.right, r.bottom)
        return capture_png_base64(
            scale=r.scale,
            include_cursor=r.include_cursor,
            crop=crop,
            exclude_overlay=r.exclude_overlay,
        )

    # @wrap_pyautogui
    async def get_cursor_position(self, r: GetCursorPositionRequest):
        pos = get_cursor_position()
        x, y = pos["x"], pos["y"]
        return {"PositionX": x, "PositionY": y}

    async def get_screen_size(self, r: GetScreenSizeRequest):
        x, y = pyautogui.size()
        return {"Width": x, "Height": y}

    async def change_password(self, r: ChangePasswordRequest):
        return await change_password(r.username, r.new_password)

    @wrap_pyautogui_async
    def type_at(self, r: TypeAtRequest):
        pyautogui.click(r.x, r.y)
        if r.caret_position == "start":
            pyautogui.press("home")
        elif r.caret_position == "end":
            pyautogui.press("end")
        if r.clear:
            pyautogui.hotkey("ctrl", "a")
            pyautogui.press("backspace")
        paste(r.text)
        if r.press_enter:
            pyautogui.press("enter")
        return f"typed at ({r.x},{r.y})"

    @wrap_pyautogui_async
    def shortcut(self, r: ShortcutRequest):
        keys = [
            SHORTCUT_KEY_ALIASES.get(key.strip().lower(), key.strip().lower())
            for key in r.shortcut.replace(" ", "").split("+")
            if key.strip()
        ]
        if not keys:
            raise BaseError("shortcut is required")
        return pyautogui.hotkey(*keys)

    async def wait_for(self, r: WaitForRequest):
        timeout = max(0.1, min(float(r.timeout), 120.0))
        interval = max(0.05, min(float(r.interval), 5.0))
        if r.condition == "seconds":
            time.sleep(timeout)
            return {"Matched": True, "Condition": r.condition, "Elapsed": timeout}

        baseline = _screen_signature()
        started = time.monotonic()
        attempts = 0
        last_ratio = 0.0
        while time.monotonic() - started < timeout:
            attempts += 1
            time.sleep(interval)
            current = _screen_signature()
            last_ratio = _screen_diff_ratio(baseline, current)
            if last_ratio >= r.threshold:
                return {
                    "Matched": True,
                    "Condition": r.condition,
                    "Elapsed": round(time.monotonic() - started, 3),
                    "Attempts": attempts,
                    "DiffRatio": round(last_ratio, 6),
                }
        return {
            "Matched": False,
            "Condition": r.condition,
            "Elapsed": round(time.monotonic() - started, 3),
            "Attempts": attempts,
            "DiffRatio": round(last_ratio, 6),
        }

    async def show_overlay(self, r: ShowOverlayRequest):
        return show_overlay(
            x=r.x,
            y=r.y,
            target_x=r.target_x,
            target_y=r.target_y,
            label=r.label,
            duration_ms=r.duration_ms,
            color=r.color,
            radius=r.radius,
        )

    async def hide_overlay(self, r: HideOverlayRequest):
        return hide_overlay()

    async def overlay_status(self, r: OverlayStatusRequest):
        return overlay_status()

    async def overlay_self_test(self, r: OverlaySelfTestRequest):
        return overlay_self_test()
