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

import json

from mcp import types
from pydantic import Field
try:
    from tool_server_client.models import *
except ModuleNotFoundError:
    from tool_server_client.entity import *

from mcp_server.common.client import tool_server_client
from mcp_server.common.errors import handle_error
from mcp_server.common.logs import LOG
from mcp_server.tools import MCP


def call_tool_server_action(action: str, params: dict | None = None, endpoint: str = None) -> dict:
    client = tool_server_client(endpoint)
    response = client._make_request(action, params or {})
    return response.get("Result") or {}


@MCP.tool(
    name="move_mouse",
    description="Move the mouse pointer to the target position"
)
async def move_mouse(
    x: int = Field(
        default=50,
        description="X coordinate of the mouse pointer to the target position"
    ),
    y: int = Field(
        default=50,
        description="Y coordinate of the mouse pointer to the target position"
    ),
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info(f"Call move mouse, x: {x}, y: {y}")
    try:
        client = tool_server_client(endpoint)
        response = client.move_mouse(
            x=x,
            y=y,
        )

        if not response:
            return handle_error("move_mouse")

        return [
            types.TextContent(
                type="text",
                text="Operation successful"
            )
        ]

    except Exception as e:
        return handle_error("move_mouse", e)


@MCP.tool(
    name="click_mouse",
    description="Click the mouse pointer at the target position"
)
async def click_mouse(
    x: int = Field(
        default=50,
        description="X coordinate of the mouse pointer to the target position"
    ),
    y: int = Field(
        default=50,
        description="Y coordinate of the mouse pointer to the target position"
    ),
    button: str = Field(
        default="",
        description="Mouse button, optional values: Left: left button, Right: right button, Middle: middle button, DoubleLeft: double-click left button"
    ),
    endpoint: str = Field(
        default=None,
    )
):
    LOG.info(f"Call click mouse, x: {x}, y: {y}, button: {button}")
    try:
        client = tool_server_client(endpoint)
        response = client.click_mouse(
            x=x,
            y=y,
            button=button,
            press=False,
            release=False,
        )
        if not response:
            return handle_error("click_mouse")

        return [
            types.TextContent(
                type="text",
                text="Operation successful"
            )
        ]

    except Exception as e:
        return handle_error("click_mouse", e)


@MCP.tool(
    name="drag_mouse",
    description="Drag the mouse pointer from the start position to the target position"
)
async def drag_mouse(
    source_x: int = Field(
        default=50,
        description="X coordinate of the mouse pointer to the start position"
    ),
    source_y: int = Field(
        default=50,
        description="Y coordinate of the mouse pointer to the start position"
    ),
    target_x: int = Field(
        default=50,
        description="X coordinate of the mouse pointer to the target position"
    ),
    target_y: int = Field(
        default=50,
        description="Y coordinate of the mouse pointer to the target position"
    ),
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info(
        f"Call drag mouse, source_x: {source_x}, source_y: {source_y}, target_x: {target_x}, target_y: {target_y}")
    try:
        client = tool_server_client(endpoint)
        response = client.drag_mouse(
            source_x=source_x,
            source_y=source_y,
            target_x=target_x,
            target_y=target_y,
        )

        if not response:
            return handle_error("drag_mouse")

        return [
            types.TextContent(
                type="text",
                text="Operation successful"
            )
        ]

    except Exception as e:
        return handle_error("drag_mouse", e)


@MCP.tool(
    name="scroll",
    description="Scroll the mouse pointer to the target position"
)
async def scroll(
    x: int = Field(
        default=50,
        description="X coordinate of the mouse pointer to the target position"
    ),
    y: int = Field(
        default=50,
        description="Y coordinate of the mouse pointer to the target position"
    ),
    direction: str = Field(
        default=None,
        description="Scroll direction, optional values: Up, Down, Left, Right"
    ),
    amount: int = Field(
        default=0,
        description="Scroll times", ge=0, le=10
    ),
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info(
        f"Call scroll, x: {x}, y: {y}, direction: {direction}, amount: {amount}")
    try:
        client = tool_server_client(endpoint)
        response = client.scroll(
            x=x,
            y=y,
            scroll_amount=amount,
            scroll_direction=direction,
        )

        if not response:
            return handle_error("scroll")

        return [
            types.TextContent(
                type="text",
                text="Operation successful"
            )
        ]

    except Exception as e:
        return handle_error("scroll", e)


@MCP.tool(
    name="press_key",
    description="Press the specified key"
)
async def press_key(
    key: str = Field(
        default=None,
        description="Specified key, if it's multiple text, please use TypeText"
    ),
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info(f"Call press key, key: {key}")
    try:
        client = tool_server_client(endpoint)
        response = client.press_key(
            key=key,
        )

        if not response:
            return handle_error("press_key")

        return [
            types.TextContent(
                type="text",
                text="Operation successful"
            )
        ]

    except Exception as e:
        return handle_error("press_key", e)


@MCP.tool(
    name="type_text",
    description="Type the specified text"
)
async def type_text(
    text: str = Field(
        default=None,
        description="Clipboard content, string length limit 100"
    ),
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info(f"Call type text, text: {text}")
    try:
        client = tool_server_client(endpoint)
        response = client.type_text(
            text=text,
        )

        if not response:
            return handle_error("type_text")

        return [
            types.TextContent(
                type="text",
                text="Operation successful"
            )
        ]

    except Exception as e:
        return handle_error("type_text", e)


@MCP.tool(
    name="get_cursor_position",
    description="Get the current cursor position"
)
async def get_cursor_position(
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info("Call get cursor position")
    try:
        client = tool_server_client(endpoint)
        response = client.get_cursor_position()

        if not response:
            return handle_error("get_cursor_position")

        LOG.info(
            f"Get cursor position, x: {response.Result.x}, y: {response.Result.y}")

        return [
            types.TextContent(
                type="text",
                text=str(
                    {
                        "x": response.Result.x,
                        "y": response.Result.y,
                    }
                )
            )
        ]

    except Exception as e:
        return handle_error("get_cursor_position", e)


@MCP.tool(
    name="screenshot",
    description="Take a screenshot of the current screen"
)
async def screenshot(
    scale: float = Field(
        default=1.0,
        description="Screenshot scale from 0.1 to 1.0"
    ),
    include_cursor: bool = Field(
        default=False,
        description="Draw cursor marker on the returned screenshot"
    ),
    exclude_overlay: bool = Field(
        default=True,
        description="Hide runtime overlay during screenshot capture"
    ),
    left: int = Field(default=None, description="Optional crop left"),
    top: int = Field(default=None, description="Optional crop top"),
    right: int = Field(default=None, description="Optional crop right"),
    bottom: int = Field(default=None, description="Optional crop bottom"),
    endpoint: str = Field(
        default=None,
    ),
):
    LOG.info("Call screenshot")
    try:
        params = {
            "Scale": scale,
            "IncludeCursor": include_cursor,
            "ExcludeOverlay": exclude_overlay,
        }
        if None not in (left, top, right, bottom):
            params.update({"Left": left, "Top": top, "Right": right, "Bottom": bottom})
        result = call_tool_server_action("TakeScreenshot", params, endpoint)
        image = result.get("Screenshot")
        if not image:
            return handle_error("screenshot")
        width = result.get("OriginalWidth") or result.get("Width")
        height = result.get("OriginalHeight") or result.get("Height")
        LOG.info(f"Get screen size, width: {width}, height: {height}")

        return [
            types.TextContent(
                type="text",
                text=json.dumps(
                    {
                        "width": width,
                        "height": height,
                        "returned_width": result.get("Width"),
                        "returned_height": result.get("Height"),
                        "scale": result.get("Scale"),
                        "region": result.get("Region"),
                        "backend": result.get("Backend"),
                        "capture_ms": result.get("CaptureMs"),
                        "dpi_awareness": result.get("DpiAwareness"),
                        "overlay_guard": result.get("OverlayGuard"),
                    },
                    ensure_ascii=False,
                )
            ),
            types.ImageContent(
                type="image",
                data=image,
                mimeType="image/png",
            )
        ]

    except Exception as e:
        return handle_error("screenshot", e)


@MCP.tool(
    name="type_at",
    description="Click a VLM-grounded screen coordinate and type text there"
)
async def type_at(
    x: int = Field(description="X coordinate selected by the VLM"),
    y: int = Field(description="Y coordinate selected by the VLM"),
    text: str = Field(description="Text to type"),
    clear: bool = Field(default=False, description="Clear existing field content first"),
    caret_position: str = Field(default="idle", description="start, idle, or end"),
    press_enter: bool = Field(default=False, description="Press Enter after typing"),
    endpoint: str = Field(default=None),
):
    try:
        result = call_tool_server_action(
            "TypeAt",
            {
                "PositionX": x,
                "PositionY": y,
                "Text": text,
                "Clear": clear,
                "CaretPosition": caret_position,
                "PressEnter": press_enter,
            },
            endpoint,
        )
        return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]
    except Exception as e:
        return handle_error("type_at", e)


@MCP.tool(
    name="shortcut",
    description="Press a keyboard shortcut such as ctrl+c, alt+tab, or win+r"
)
async def shortcut(
    shortcut: str = Field(description="Shortcut with keys separated by +"),
    endpoint: str = Field(default=None),
):
    try:
        result = call_tool_server_action("Shortcut", {"Shortcut": shortcut}, endpoint)
        return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]
    except Exception as e:
        return handle_error("shortcut", e)


@MCP.tool(
    name="wait_for",
    description="Wait for a VLM-relevant condition: screen_changed or seconds"
)
async def wait_for(
    condition: str = Field(default="screen_changed", description="screen_changed or seconds"),
    timeout: float = Field(default=10.0, description="Timeout seconds"),
    interval: float = Field(default=0.25, description="Polling interval seconds"),
    threshold: float = Field(default=0.002, description="Screenshot difference threshold"),
    endpoint: str = Field(default=None),
):
    try:
        result = call_tool_server_action(
            "WaitFor",
            {
                "Condition": condition,
                "Timeout": timeout,
                "Interval": interval,
                "Threshold": threshold,
            },
            endpoint,
        )
        return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]
    except Exception as e:
        return handle_error("wait_for", e)
