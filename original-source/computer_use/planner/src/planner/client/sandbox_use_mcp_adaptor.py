# -*- coding: utf-8 -*-
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


"""
Adapters for converting AI model actions to MCP tool calls for sandbox interaction.

This module provides classes to:
1. Parse action calls from specific AI model responses (e.g., Doubao UI Tars).
2. Convert these parsed actions into standardized MCP (Multi-Control Platform) tool calls.
3. Execute these MCP tool calls within a sandbox environment via an MCP session.
"""

import logging
import re
from abc import ABC, abstractmethod
from typing import Tuple, Optional, Dict, Any, TypedDict

from mcp import ClientSession


class McpToolCall(TypedDict):
    name: str
    arguments: Dict[str, Any] | None


class ConvertedToolCall(TypedDict):
    tool_call: McpToolCall
    coordinate_transform: Dict[str, Any]


class ModelActionCallToMCPToolCallAdaptor(ABC):
    """
    To convert the action call returned by the Chat model into an MCP tool call

    for example, transforming click(100, 200) into click_mouse(100, 200)
    """

    @abstractmethod
    def to_mcp_tool_call(
            self, action_call: str, screen: Dict[str, Any]) -> McpToolCall:
        pass


def _runtime_region(screen: Dict[str, Any]) -> Dict[str, int]:
    region = dict((screen or {}).get("region") or {})
    width = int(region.get("width") or (screen or {}).get("width") or 0)
    height = int(region.get("height") or (screen or {}).get("height") or 0)
    left = int(region.get("left") or 0)
    top = int(region.get("top") or 0)
    if not width and "right" in region:
        width = int(region["right"]) - left
    if not height and "bottom" in region:
        height = int(region["bottom"]) - top
    if width <= 0 or height <= 0:
        raise ValueError(f"Invalid screenshot region for coordinate conversion: {screen}")
    return {
        "left": left,
        "top": top,
        "width": width,
        "height": height,
    }


def _validate_normalized_point(x: int | str, y: int | str) -> tuple[int, int]:
    try:
        nx, ny = int(x), int(y)
    except (TypeError, ValueError):
        raise ValueError(f"Invalid normalized point: ({x}, {y})")
    if nx < 0 or nx > 1000 or ny < 0 or ny > 1000:
        raise ValueError(f"Normalized point out of range 0..1000: ({nx}, {ny})")
    return nx, ny


def _point_converter(screen: Dict[str, Any]):
    region = _runtime_region(screen)
    points: list[dict[str, Any]] = []

    def point(x: int | str, y: int | str) -> tuple[int, int]:
        nx, ny = _validate_normalized_point(x, y)
        absolute_x = region["left"] + min(region["width"] - 1, int(nx * region["width"] / 1000))
        absolute_y = region["top"] + min(region["height"] - 1, int(ny * region["height"] / 1000))
        if absolute_x < region["left"] or absolute_x >= region["left"] + region["width"]:
            raise ValueError(f"Converted x out of screenshot region: {absolute_x}, region={region}")
        if absolute_y < region["top"] or absolute_y >= region["top"] + region["height"]:
            raise ValueError(f"Converted y out of screenshot region: {absolute_y}, region={region}")
        points.append(
            {
                "normalized": {"x": nx, "y": ny},
                "absolute": {"x": absolute_x, "y": absolute_y},
            }
        )
        return absolute_x, absolute_y

    def trace() -> Dict[str, Any]:
        return {
            "input_space": "normalized_0_1000",
            "output_space": "desktop_absolute",
            "region": region,
            "points": points,
        }

    return point, trace


def _strip_wrappers(text: str) -> str:
    text = (text or "").strip()
    fence = re.search(r"```(?:[a-zA-Z0-9_-]+)?\s*(?P<body>[\s\S]*?)```", text)
    if fence:
        text = fence.group("body").strip()
    return text.strip().strip("`").strip()


def _normalize_action_text(text: str) -> str:
    text = _strip_wrappers(text)
    text = text.replace("：", ":").replace("，", ",").replace("（", "(").replace("）", ")")
    text = text.replace("<|box_start|>", "").replace("<|box_end|>", "")
    return text.strip()


def _extract_action_call(text: str) -> Optional[str]:
    text = _normalize_action_text(text)
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    for line in reversed(lines or [text]):
        line = re.sub(r"^(?:Action|动作)\s*:\s*", "", line, flags=re.IGNORECASE).strip()
        if re.match(r"^\w+\s*\(", line):
            return line
    m = re.search(r"(?P<call>\w+\s*\([\s\S]*\))", text)
    if m:
        return m.group("call").strip()
    return None


class DoubaoUITarsToComputerUseMCPAdaptor(ModelActionCallToMCPToolCallAdaptor):
    def __init__(self, layout_pattern=None, action_pattern=None, args_patterns=None):
        self.logger = logging.getLogger(self.__class__.__name__)
        if layout_pattern is None:
            layout_pattern = r'Action_Summary[:：](?P<summary>[\s\S]*)\nAction:(?P<action>.*)'
        if action_pattern is None:
            action_pattern = r'(?P<action>\w+)\(\s*(?P<args>.*)\)'
        if args_patterns is None:
            args_patterns = r'''
            click: start_box=\s*\'<bbox>(?P<left>\d+)\s+(?P<top>\d+)\s+(?P<bottom>\d+)\s+(?P<right>\d+)</bbox>\'
            drag: start_box=\s*\'<bbox>(?P<start_left>\d+)\s+(?P<start_top>\d+)\s+(?P<start_bottom>\d+)\s+(?P<start_right>\d+)</bbox>\',\s+end_box=\s*\'<bbox>(?P<end_left>\d+)\s+(?P<end_top>\d+)\s+(?P<end_bottom>\d+)\s+(?P<end_right>\d+)</bbox>\'
            type: content=\'(?P<content>.*)\'
            hotkey: key=\'(?P<keys>.*)\'
            scroll: direction=\'(?P<direction>[^']*)\'(,\s+start_box=\'<bbox>(?P<left>\d+)\s+(?P<top>\d+)\s+(?P<bottom>\d+)\s+(?P<right>\d+)</bbox>\')?
            '''
        self.layout_pattern = re.compile(layout_pattern)
        self.action_pattern = re.compile(action_pattern)
        action_args_pattern = [line.split(":", 1) for line in args_patterns.strip().splitlines()]
        self.args_pattern_map = {action.strip(): re.compile(pattern.strip()) for (action, pattern) in
                                 action_args_pattern}

    def parse_summary_and_action_from_model_response(self, text: str) -> Tuple[Optional[str], Optional[str]]:
        m = next(self.layout_pattern.finditer(text), None)
        if m is not None:
            summary, action = m.groupdict()["summary"], m.groupdict()["action"]
            return summary.strip(), _extract_action_call(action)
        action = _extract_action_call(text)
        if action is not None:
            return "模型返回动作", action
        return None, None

    def _parse_action_call(self, text) -> Tuple[Optional[str], Optional[Dict[str, Any]]]:
        action_match = self.action_pattern.match(_normalize_action_text(text))
        if action_match is None:
            self.logger.debug("text does not match action call, text=%s, pattern=%s",
                              text, self.action_pattern)
            return None, None
        action = action_match.group('action')
        args = action_match.group('args')
        self.logger.debug("action=%-17s, args=%s", action, args)
        kwargs = {}
        if action not in ('wait', 'finished', 'call_user'):
            key = 'click' if action in ('click', 'left_double_click', 'right_click') else action
            args_pattern = self.args_pattern_map[key]
            m = args_pattern.match(args)
            if m is None:
                self.logger.info("args does not match, args=%s, pattern=%s", args, args_pattern)
            else:
                kwargs = m.groupdict()
                self.logger.debug("kwargs=%s", kwargs)
        return action, kwargs

    def to_mcp_tool_call(self, action_call: str, screen: Dict[str, Any]) -> McpToolCall:
        converted = self.to_mcp_tool_call_with_trace(action_call, screen)
        return converted["tool_call"]

    def to_mcp_tool_call_with_trace(self, action_call: str, screen: Dict[str, Any]) -> ConvertedToolCall:
        point, coordinate_trace = _point_converter(screen)
        action, args = self._parse_action_call(action_call)
        self.logger.info("Converting action: %s, kwargs: %s", action, args)
        if action in ("click", "left_double_click", "right_click"):
            x, y = point(args['left'], args['top'])
            button = "left" if action == "click" else "double_left" if action == "left_double_click" else "right"
            tool_name, tool_kwargs = "click_mouse", {"x": x, "y": y, "button": button}
            self.logger.debug("tool_name=%s, tool_kwargs=%s", tool_name, tool_kwargs)
        elif action == "drag":
            sx, sy = point(args['start_left'], args['start_top'])
            tx, ty = point(args['end_left'], args['end_top'])
            tool_name = "drag_mouse"
            tool_kwargs = {"source_x": sx, "source_y": sy, "target_x": tx, "target_y": ty}
        elif action == "type":
            content = args['content'].replace('\\n', '\n').replace('\\"', '"').replace("\\'", "'")
            tool_name, tool_kwargs = "type_text", {"text": content}
        elif action == "hotkey":
            tool_name, tool_kwargs = "press_key", {"key": args['keys']}
        elif action == "scroll":
            x, y = point(args.get('left') or 500, args.get('top') or 500)
            tool_name = "scroll"
            tool_kwargs = {"x": x, "y": y, "direction": args['direction'], "amount": 3}
        elif action == "wait":
            tool_name, tool_kwargs = "wait", {}
        elif action == "finished":
            tool_name, tool_kwargs = "finished", None
        elif action == "call_user":
            tool_name, tool_kwargs = "call_user", None
        else:
            raise ValueError(f"Unknown action type: {action}")
        return ConvertedToolCall(
            tool_call=McpToolCall(name=tool_name, arguments=tool_kwargs),
            coordinate_transform=coordinate_trace(),
        )


class DoubaoSeedToComputerUseMCPAdaptor(ModelActionCallToMCPToolCallAdaptor):
    def __init__(self):
        self.logger = logging.getLogger(self.__class__.__name__)
        self.layout_pattern = re.compile(
            r'Thought[:：](?P<summary>[\s\S]*?)\nAction[:：]\s*(?P<action>.*)',
            re.IGNORECASE,
        )
        self.action_pattern = re.compile(r'(?P<action>\w+)\(\s*(?P<args>.*)\)\s*$', re.DOTALL)
        self.point_pattern = re.compile(r"<point>\s*(?P<x>\d+)\s+(?P<y>\d+)\s*</point>")

    def parse_summary_and_action_from_model_response(self, text: str) -> Tuple[Optional[str], Optional[str]]:
        m = next(self.layout_pattern.finditer(text), None)
        if m is not None:
            return m.group("summary").strip(), _extract_action_call(m.group("action"))
        action = _extract_action_call(text)
        if action is not None:
            return "模型返回动作", action
        return None, None

    def _parse_action_call(self, text: str) -> Tuple[Optional[str], Dict[str, Any]]:
        action_match = self.action_pattern.match(_normalize_action_text(text))
        if action_match is None:
            self.logger.debug("text does not match action call, text=%s", text)
            return None, {}
        action = action_match.group("action")
        args = action_match.group("args")
        kwargs: Dict[str, Any] = {}

        if action in ("click", "left_double", "right_single", "scroll"):
            point_match = self.point_pattern.search(args)
            if point_match:
                kwargs.update(point_match.groupdict())
        if action == "drag":
            points = list(self.point_pattern.finditer(args))
            if len(points) >= 2:
                kwargs.update({
                    "start_x": points[0].group("x"),
                    "start_y": points[0].group("y"),
                    "end_x": points[1].group("x"),
                    "end_y": points[1].group("y"),
                })
        if action == "hotkey":
            m = re.search(r"key\s*=\s*(['\"])(?P<keys>.*?)\1", args, re.DOTALL)
            if m:
                kwargs["keys"] = m.group("keys")
        if action == "type":
            m = re.search(r"content\s*=\s*(['\"])(?P<content>.*?)\1", args, re.DOTALL)
            if m:
                kwargs["content"] = m.group("content")
        if action == "scroll":
            m = re.search(r"direction\s*=\s*(['\"])(?P<direction>.*?)\1", args, re.DOTALL)
            if m:
                kwargs["direction"] = m.group("direction")

        return action, kwargs

    def to_mcp_tool_call(self, action_call: str, screen: Dict[str, Any]) -> McpToolCall:
        converted = self.to_mcp_tool_call_with_trace(action_call, screen)
        return converted["tool_call"]

    def to_mcp_tool_call_with_trace(self, action_call: str, screen: Dict[str, Any]) -> ConvertedToolCall:
        point, coordinate_trace = _point_converter(screen)
        action, args = self._parse_action_call(action_call)
        self.logger.info("Converting seed action: %s, kwargs: %s", action, args)

        if action in ("click", "left_double", "right_single"):
            button = "left" if action == "click" else "double_left" if action == "left_double" else "right"
            tool_name = "click_mouse"
            x, y = point(args["x"], args["y"])
            tool_kwargs = {"x": x, "y": y, "button": button}
        elif action == "drag":
            tool_name = "drag_mouse"
            sx, sy = point(args["start_x"], args["start_y"])
            tx, ty = point(args["end_x"], args["end_y"])
            tool_kwargs = {
                "source_x": sx,
                "source_y": sy,
                "target_x": tx,
                "target_y": ty,
            }
        elif action == "type":
            content = args["content"].replace("\\n", "\n").replace('\\"', '"').replace("\\'", "'")
            tool_name, tool_kwargs = "type_text", {"text": content}
        elif action == "hotkey":
            tool_name, tool_kwargs = "press_key", {"key": args["keys"]}
        elif action == "scroll":
            tool_name = "scroll"
            x, y = point(args.get("x") or 500, args.get("y") or 500)
            tool_kwargs = {
                "x": x,
                "y": y,
                "direction": args["direction"],
                "amount": 3,
            }
        elif action == "wait":
            tool_name, tool_kwargs = "wait", {}
        elif action == "finished":
            tool_name, tool_kwargs = "finished", None
        elif action == "call_user":
            tool_name, tool_kwargs = "call_user", None
        else:
            raise ValueError(f"Unknown action type: {action}")
        return ConvertedToolCall(
            tool_call=McpToolCall(name=tool_name, arguments=tool_kwargs),
            coordinate_transform=coordinate_trace(),
        )


class SandboxMCPToolCallAdaptor(ABC):
    """
    沙箱MCP工具调用适配器
    """

    @abstractmethod
    async def call(self, tool_call: McpToolCall, sandbox_tool_server_endpoint=None):
        pass


class ComputerUseSandboxMCPToolCallAdaptor(SandboxMCPToolCallAdaptor):

    def __init__(self, mcp_session: ClientSession):
        self.mcp_session = mcp_session

    async def call(self, tool_call: McpToolCall, sandbox_tool_server_endpoint=None):
        if sandbox_tool_server_endpoint:
            tool_call["arguments"]["endpoint"] = sandbox_tool_server_endpoint
        return await self.mcp_session.call_tool(**tool_call)
