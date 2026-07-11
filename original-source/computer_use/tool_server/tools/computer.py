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

import logging
import time

from .dpi_awareness import enable_dpi_awareness

enable_dpi_awareness()

import pyautogui
import pyperclip

from abc import ABC, abstractmethod
from typing import Literal, Tuple
from .base import BaseResult, BaseError, snake_to_camel, camel_to_snake
from pydantic import BaseModel, Field

logger = logging.getLogger(__name__)

DRAG_STEP = 30
class MBaseModel(BaseModel):
    class Config:
        populate_by_name = True

class MoveMouseRequest(MBaseModel):
    x: int = Field(0, description="x position", alias="PositionX")
    y: int = Field(0, description="y position", alias="PositionY")

class ClickMouseRequest(MBaseModel):
    x: int = Field(0, description="x position", alias="PositionX")
    y: int = Field(0, description="y position", alias="PositionY")
    button: Literal["left", "right", "middle", "double_click", "double_left"] = Field(
        "left", alias="Button"
    )
    press: bool = Field(False, description="press mouse", alias="Press")
    release: bool = Field(False, description="release mouse", alias="Release")

class PressMouseRequest(MBaseModel):
    x: int = Field(0, description="x position", alias="PositionX")
    y: int = Field(0, description="y position", alias="PositionY")
    button: Literal["left", "right", "middle"] = Field(
        "left", alias="Button"
    )

class ReleaseMouseRequest(MBaseModel):
    x: int = Field(0, description="x position", alias="PositionX")
    y: int = Field(0, description="y position", alias="PositionY")
    button: Literal["left", "right", "middle"] = Field(
        "left", alias="Button"
    )

class DragMouseRequest(MBaseModel):
    source_x: int = Field(0, description="source x position", alias="SourceX")
    source_y: int = Field(0, description="source y position", alias="SourceY")
    target_x: int = Field(0, description="target x position", alias="TargetX")
    target_y: int = Field(0, description="target y position", alias="TargetY")

class ScrollRequest(MBaseModel):
    scroll_direction: Literal["up", "down", "left", "right"] = Field(
        "up", alias="Direction"
    )
    scroll_amount: int = Field(0, description="scroll amount", alias="Amount")
    x: int = Field(0, description="x position", alias="PositionX")
    y: int = Field(0, description="y position", alias="PositionY")


class PressKeyRequest(MBaseModel):
    key: str = Field("", description="key", alias="Key")


class TypeTextRequest(MBaseModel):
    text: str = Field("", description="text", alias="Text")


class WaitRequest(MBaseModel):
    duration: int = Field(0, description="duration", alias="Duration")


class TakeScreenshotRequest(MBaseModel):
    scale: float = Field(1.0, description="screenshot scale from 0.1 to 1.0", alias="Scale")
    include_cursor: bool = Field(False, description="draw cursor marker on screenshot", alias="IncludeCursor")
    exclude_overlay: bool = Field(True, description="hide runtime overlay during screenshot capture", alias="ExcludeOverlay")
    left: int | None = Field(None, description="crop left", alias="Left")
    top: int | None = Field(None, description="crop top", alias="Top")
    right: int | None = Field(None, description="crop right", alias="Right")
    bottom: int | None = Field(None, description="crop bottom", alias="Bottom")


class GetCursorPositionRequest(MBaseModel):
    pass


class GetScreenSizeRequest(MBaseModel):
    pass


class ChangePasswordRequest(MBaseModel):
    username: str = Field("", description="username", alias="Username")
    new_password: str = Field("", description="new password", alias="NewPassword")


class TypeAtRequest(MBaseModel):
    x: int = Field(0, description="x position", alias="PositionX")
    y: int = Field(0, description="y position", alias="PositionY")
    text: str = Field("", description="text", alias="Text")
    clear: bool = Field(False, description="clear existing text first", alias="Clear")
    caret_position: Literal["start", "idle", "end"] = Field("idle", alias="CaretPosition")
    press_enter: bool = Field(False, description="press Enter after typing", alias="PressEnter")


class ShortcutRequest(MBaseModel):
    shortcut: str = Field("", description="shortcut such as ctrl+c or alt+tab", alias="Shortcut")


class WaitForRequest(MBaseModel):
    condition: Literal["screen_changed", "seconds"] = Field("screen_changed", alias="Condition")
    timeout: float = Field(10.0, description="timeout seconds", alias="Timeout")
    interval: float = Field(0.25, description="poll interval seconds", alias="Interval")
    threshold: float = Field(0.002, description="image difference threshold", alias="Threshold")


class ShowOverlayRequest(MBaseModel):
    x: int = Field(0, description="desktop absolute x position", alias="PositionX")
    y: int = Field(0, description="desktop absolute y position", alias="PositionY")
    target_x: int | None = Field(None, description="optional drag target x", alias="TargetX")
    target_y: int | None = Field(None, description="optional drag target y", alias="TargetY")
    label: str = Field("", description="short overlay label", alias="Label")
    duration_ms: int = Field(1500, description="display duration in milliseconds", alias="DurationMs")
    color: str = Field("#ff2f4f", description="marker color", alias="Color")
    radius: int = Field(32, description="marker radius in pixels", alias="Radius")


class HideOverlayRequest(MBaseModel):
    pass


class OverlayStatusRequest(MBaseModel):
    pass


class OverlaySelfTestRequest(MBaseModel):
    pass


class IComputerTool(ABC):
    @abstractmethod
    def move_mouse(self, request: MoveMouseRequest):
        pass

    @abstractmethod
    def click_mouse(self, request: ClickMouseRequest):
        pass

    @abstractmethod
    def press_mouse(self, request: PressMouseRequest):
        pass

    @abstractmethod
    def release_mouse(self, request: ReleaseMouseRequest):
        pass

    @abstractmethod
    async def drag_mouse(self, request: DragMouseRequest):
        pass

    @abstractmethod
    def scroll(self, request: ScrollRequest):
        pass

    @abstractmethod
    def press_key(self, request: PressKeyRequest):
        pass

    @abstractmethod
    def type_text(self, request: TypeTextRequest):
        pass

    @abstractmethod
    def wait(self, request: WaitRequest):
        pass

    @abstractmethod
    def take_screenshot(self, request: TakeScreenshotRequest) -> BaseResult:
        pass

    @abstractmethod
    def get_cursor_position(self, request: GetCursorPositionRequest) -> Tuple[int, int]:
        pass

    @abstractmethod
    def get_screen_size(self, request: GetScreenSizeRequest) -> Tuple[int, int]:
        pass

    @abstractmethod
    def change_password(self, req: ChangePasswordRequest):
        pass

    @abstractmethod
    def type_at(self, req: TypeAtRequest):
        pass

    @abstractmethod
    def shortcut(self, req: ShortcutRequest):
        pass

    @abstractmethod
    def wait_for(self, req: WaitForRequest):
        pass

    @abstractmethod
    def show_overlay(self, req: ShowOverlayRequest):
        pass

    @abstractmethod
    def hide_overlay(self, req: HideOverlayRequest):
        pass

    @abstractmethod
    def overlay_status(self, req: OverlayStatusRequest):
        pass

    @abstractmethod
    def overlay_self_test(self, req: OverlaySelfTestRequest):
        pass


def chunks(s: str, chunk_size: int) -> list[str]:
    return [s[i: i + chunk_size] for i in range(0, len(s), chunk_size)]


def paste(foo):
    original_clipboard = None
    restore_clipboard = True
    try:
        original_clipboard = pyperclip.paste()
    except Exception:
        restore_clipboard = False
    try:
        pyperclip.copy(foo)
        time.sleep(0.05)
        pyautogui.hotkey('ctrl', 'v')
        time.sleep(0.05)
    finally:
        if restore_clipboard:
            try:
                pyperclip.copy(original_clipboard)
            except Exception:
                logger.warning("Failed to restore clipboard after paste", exc_info=True)


def gen_path(source_x, source_y, target_x, target_y):
    drag_path = [[source_x, source_y]]
    dx = target_x - source_x
    dy = target_y - source_y
    steps = max(abs(int(dx / DRAG_STEP)), abs(int(dy / DRAG_STEP)))
    for i in range(steps):
        x = source_x + int(dx * i / steps)
        y = source_y + int(dy * i / steps)
        drag_path.append([x, y])
    drag_path.append([target_x, target_y])
    return drag_path


def generate_request(action: str, all_params: dict):
    model_cls = globals().get(f"{snake_to_camel(action)}Request")
    params = {k: camel_to_snake(v) for k, v in all_params.items()}
    if model_cls:
        return model_cls(**params)
    raise BaseError(f"request {snake_to_camel(action)}Request not found")
