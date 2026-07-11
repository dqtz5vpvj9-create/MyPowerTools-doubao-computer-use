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
Task Planning and Execution Service

This module defines the Planner class responsible for orchestrating the
automation task execution loop. It interacts with the AI model for action
planning, the MCP session for environment interaction (screenshots, tool calls),
and UI adapters for formatting output.
"""

import asyncio
import base64
import binascii
import json
import logging
import urllib.parse
import urllib.request
from typing import Any, AsyncGenerator, Tuple
from mcp import ClientSession

from client.model_client import ChatModelClient
from client.sandbox_use_mcp_adaptor import (McpToolCall, DoubaoUITarsToComputerUseMCPAdaptor,
                                            DoubaoSeedToComputerUseMCPAdaptor,
                                            ComputerUseSandboxMCPToolCallAdaptor)
from common.config import get_settings, get_models
from common.ui_interface import ComputerUseUIInterface
from common.constants import (
    COMPUTER_USE,
    MODEL_DOUBAO_UI_TARS,
    MODEL_DOUBAO_SEED_2_0_LITE,
    MODEL_DOUBAO_SEED_2_1_TURBO,
    MODEL_DOUBAO_SEED_2_1_PRO,
)

MODEL_TOOL_CALL_ADAPTER_MAP = {
    COMPUTER_USE: {
        MODEL_DOUBAO_UI_TARS: DoubaoUITarsToComputerUseMCPAdaptor,
        MODEL_DOUBAO_SEED_2_0_LITE: DoubaoSeedToComputerUseMCPAdaptor,
        MODEL_DOUBAO_SEED_2_1_TURBO: DoubaoSeedToComputerUseMCPAdaptor,
        MODEL_DOUBAO_SEED_2_1_PRO: DoubaoSeedToComputerUseMCPAdaptor,
    }
}

USE_TYPE_UI_ADAPTER_MAP = {
    COMPUTER_USE: ComputerUseUIInterface,
}

USE_TYPE_TOOL_CALL_ADAPTER_MAP = {
    COMPUTER_USE: ComputerUseSandboxMCPToolCallAdaptor
}

class Planner(object):
    def __init__(self, model_client: ChatModelClient, mcp_session: ClientSession, task_id: str,
                 sandbox_endpoint:str, use_type: str = COMPUTER_USE):
        self.logger = logging.getLogger(self.__class__.__name__)
        self.model_client = model_client
        self.mcp_session = mcp_session
        self.task_id = task_id
        self.max_actions = get_models().get(model_client.model_name).max_action
        self.step_interval = get_settings().planner.step_interval
        self.wait_interval = get_settings().planner.action_wait_interval

        self.model_action_adaptor = MODEL_TOOL_CALL_ADAPTER_MAP[use_type][model_client.model_name]()
        self.ui_adaptor = USE_TYPE_UI_ADAPTER_MAP[use_type]()
        self.sandbox_tool_call_adapter = USE_TYPE_TOOL_CALL_ADAPTER_MAP[use_type](self.mcp_session)
        self.tool_server_endpoint = sandbox_endpoint

    def _normalize_screen_metadata(self, raw: dict) -> dict:
        width = int(raw.get("width") or raw.get("Width") or raw.get("returned_width") or 0)
        height = int(raw.get("height") or raw.get("Height") or raw.get("returned_height") or 0)
        region = dict(raw.get("region") or {})
        region_left = int(region.get("left") or 0)
        region_top = int(region.get("top") or 0)
        region_width = int(region.get("width") or width)
        region_height = int(region.get("height") or height)
        if not region_width and "right" in region:
            region_width = int(region["right"]) - region_left
        if not region_height and "bottom" in region:
            region_height = int(region["bottom"]) - region_top
        region.update(
            {
                "left": region_left,
                "top": region_top,
                "right": int(region.get("right") or (region_left + region_width)),
                "bottom": int(region.get("bottom") or (region_top + region_height)),
                "width": region_width,
                "height": region_height,
            }
        )
        return {
            "width": width,
            "height": height,
            "returned_width": raw.get("returned_width") or raw.get("ReturnedWidth"),
            "returned_height": raw.get("returned_height") or raw.get("ReturnedHeight"),
            "scale": raw.get("scale") or raw.get("Scale"),
            "region": region,
            "backend": raw.get("backend") or raw.get("Backend"),
            "capture_ms": raw.get("capture_ms") or raw.get("CaptureMs"),
            "dpi_awareness": raw.get("dpi_awareness") or raw.get("DpiAwareness"),
            "overlay_guard": raw.get("overlay_guard") or raw.get("OverlayGuard"),
        }

    def _validate_screenshot_payload(self, screen: dict, image_base64: str) -> None:
        region = screen.get("region") or {}
        if int(screen.get("width") or 0) <= 0 or int(screen.get("height") or 0) <= 0:
            raise ValueError(f"Invalid screenshot dimensions: {screen}")
        if int(region.get("width") or 0) <= 0 or int(region.get("height") or 0) <= 0:
            raise ValueError(f"Invalid screenshot region: {screen}")
        if not image_base64:
            raise ValueError("Screenshot image payload is empty")
        try:
            decoded = base64.b64decode(image_base64, validate=True)
        except (binascii.Error, ValueError) as e:
            raise ValueError(f"Screenshot image payload is not valid base64: {e}") from e
        if len(decoded) < 100:
            raise ValueError("Screenshot image payload is too small")

    async def _take_screenshot(self) -> Tuple[dict, str]:
        """
        Capture screen screenshot

        Returns:
            Tuple[dict, str]: runtime screenshot metadata and image base64 data
        """
        last_error: Exception | None = None
        for attempt in range(1, 4):
            try:
                result = await self.sandbox_tool_call_adapter.call(
                    McpToolCall(name="screenshot", arguments={}), self.tool_server_endpoint)
                self.logger.debug("screenshot result=%s", result)
                if (len(result.content) == 0 or result.content[0].type != "text" or
                        result.content[0].text == "{}" or "Error" in result.content[0].text):
                    raise ValueError("Screenshot Failed")
                size = json.loads(result.content[0].text)
                screen = self._normalize_screen_metadata(size)
                image_base64 = result.content[1].data if len(result.content) > 1 else ""
                self._validate_screenshot_payload(screen, image_base64)
                self.logger.info("Screenshot captured - attempt=%s, screen=%s", attempt, screen)
                return screen, image_base64
            except Exception as e:
                last_error = e
                self.logger.warning("Screenshot attempt failed, attempt=%s, error=%s", attempt, e)
                if attempt < 3:
                    await asyncio.sleep(0.5)
        raise ValueError(f"Screenshot Failed after retries: {last_error}")

    def _format_sse(self, data: dict | None = None, **kwargs) -> str:
        """
        Format data to SSE compliant string

        Args:
            data: Initial data dictionary
            **kwargs: Additional fields to merge

        Returns:
            str: Formatted SSE string
        """
        if not data:
            data = {}
        data.update(kwargs)
        data['task_id'] = self.task_id
        return f"data: {json.dumps(data)}\n\n"

    def _summarize_tool_result(self, result: Any) -> list[dict]:
        summary = []
        for content in getattr(result, "content", []) or []:
            item = {"type": getattr(content, "type", None)}
            if item["type"] == "text":
                item["text"] = getattr(content, "text", "")
            elif item["type"] == "image":
                item["mimeType"] = getattr(content, "mimeType", None)
                item["data_omitted"] = True
            summary.append(item)
        return summary

    def _call_tool_server_action(self, action: str, params: dict[str, Any], timeout: float = 1.0) -> dict:
        query = urllib.parse.urlencode(
            {
                "Action": action,
                "Version": "2020-04-01",
                **{
                    key: value
                    for key, value in params.items()
                    if value is not None
                },
            }
        )
        url = f"{self.tool_server_endpoint}/?{query}"
        with urllib.request.urlopen(url, timeout=timeout) as response:
            payload = json.loads(response.read().decode("utf-8", errors="replace"))
        return payload.get("Result") or {}

    async def _show_runtime_overlay(
        self,
        *,
        tool_name: str,
        tool_kwargs: dict | None,
        action_for_ui: str,
    ) -> dict | None:
        if not tool_kwargs:
            return None
        params: dict[str, Any] | None = None
        if tool_name == "click_mouse":
            params = {
                "PositionX": tool_kwargs.get("x"),
                "PositionY": tool_kwargs.get("y"),
                "Label": action_for_ui,
                "DurationMs": 1600,
            }
        elif tool_name == "drag_mouse":
            params = {
                "PositionX": tool_kwargs.get("source_x"),
                "PositionY": tool_kwargs.get("source_y"),
                "TargetX": tool_kwargs.get("target_x"),
                "TargetY": tool_kwargs.get("target_y"),
                "Label": action_for_ui,
                "DurationMs": 2200,
            }
        elif tool_name == "scroll":
            params = {
                "PositionX": tool_kwargs.get("x"),
                "PositionY": tool_kwargs.get("y"),
                "Label": action_for_ui,
                "DurationMs": 1400,
                "Color": "#38bdf8",
            }
        if params is None:
            return None
        try:
            return await asyncio.to_thread(self._call_tool_server_action, "ShowOverlay", params)
        except Exception as e:
            self.logger.warning("Failed to show runtime overlay: %s", e)
            return {"error": str(e)}

    async def run_task(self) -> AsyncGenerator[str, Any]:
        """
        Execute the task

        Yields:
            AsyncGenerator[str, Any]: SSE formatted async generator
        """
        self.logger.info("Starting task execution, task_id=%s", self.task_id)
        yield self._format_sse({"action": "开始", "task_id": self.task_id})
        try:
            for step_index in range(1, self.max_actions + 1):
                # capture screenshot
                screen, screenshot_image = await self._take_screenshot()
                image_base64 = f"data:image/png;base64,{screenshot_image}"
                yield self._format_sse(
                    {
                        "event": "screenshot",
                        "step": step_index,
                        "screen": screen,
                        "screenshot": image_base64,
                        "task_id": self.task_id,
                    }
                )

                # get next action; retry empty, unparseable, or unsafe coordinate responses on the same screenshot
                response = ""
                summary, action = None, None
                tool_call = None
                coordinate_transform = None
                conversion_error = None
                for model_attempt in range(1, 4):
                    response = self.model_client.process_screenshot_and_update_history_messages(image_base64)
                    summary, action = self.model_action_adaptor.parse_summary_and_action_from_model_response(response)
                    conversion_error = None
                    self.logger.info(
                        "model response, attempt=%s, summary=%s, action=%s",
                        model_attempt,
                        summary,
                        action,
                    )
                    if action:
                        try:
                            if hasattr(self.model_action_adaptor, "to_mcp_tool_call_with_trace"):
                                converted = self.model_action_adaptor.to_mcp_tool_call_with_trace(action, screen)
                                tool_call = converted["tool_call"]
                                coordinate_transform = converted.get("coordinate_transform")
                            else:
                                tool_call = self.model_action_adaptor.to_mcp_tool_call(action, screen)
                            break
                        except Exception as e:
                            conversion_error = str(e)
                            self.logger.info(
                                "model action conversion failed, attempt=%s, error=%s",
                                model_attempt,
                                conversion_error,
                            )
                    retry_step = {
                        "event": "agent_step_retry",
                        "step": step_index,
                        "model_attempt": model_attempt,
                        "screen": screen,
                        "model_response": response,
                        "parsed_summary": summary,
                        "parsed_action": action,
                        "conversion_error": conversion_error,
                    }
                    if model_attempt < 3:
                        retry_step["warning"] = (
                            "Doubao response did not contain a usable Action call; retrying same screenshot"
                        )
                        yield self._format_sse(retry_step)
                        continue
                    retry_step["error"] = (
                        conversion_error or
                        "Doubao response did not contain a parseable Action call after retries"
                    )
                    yield self._format_sse(retry_step)
                agent_step = {
                    "event": "agent_step",
                    "step": step_index,
                    "screen": screen,
                    "model_response": response,
                    "parsed_summary": summary,
                    "parsed_action": action,
                }
                if coordinate_transform is not None:
                    agent_step["coordinate_transform"] = coordinate_transform
                if not action or tool_call is None:
                    agent_step["error"] = conversion_error or "Doubao response did not contain a parseable Action call"
                    yield self._format_sse(agent_step)
                    raise ValueError(
                        "Doubao response did not contain a usable Action call. "
                        f"raw_response={response}"
                    )

                # execute action
                tool_name, tool_kwargs = tool_call["name"], tool_call["arguments"]
                action_for_ui = self.ui_adaptor.mcp_tool_call_to_ui(tool_name, tool_kwargs)
                overlay_result = await self._show_runtime_overlay(
                    tool_name=tool_name,
                    tool_kwargs=tool_kwargs,
                    action_for_ui=action_for_ui,
                )
                agent_step.update(
                    {
                        "action": action_for_ui,
                        "summary": summary,
                        "tool_name": tool_name,
                        "tool_arguments": tool_kwargs,
                        "coordinate_transform": coordinate_transform,
                        "overlay_result": overlay_result,
                    }
                )
                yield self._format_sse(agent_step)

                if tool_name == "wait":
                    await asyncio.sleep(get_settings().planner.action_wait_interval)
                    continue
                if tool_name == "finished":
                    break
                if tool_name == "call_user":
                    break

                self.logger.info("calling mcp tool: %s, kwargs: %s", tool_name, tool_kwargs)
                tool_result = await self.sandbox_tool_call_adapter.call(tool_call,self.tool_server_endpoint)
                yield self._format_sse(
                    {
                        "event": "tool_result",
                        "step": step_index,
                        "tool_name": tool_name,
                        "tool_arguments": tool_kwargs,
                        "coordinate_transform": coordinate_transform,
                        "tool_result": self._summarize_tool_result(tool_result),
                        "task_id": self.task_id,
                    }
                )

                # sleep several seconds
                await asyncio.sleep(self.step_interval)
        except Exception as e:
            self.logger.exception("Task execution failed, error=%s", e)
            yield self._format_sse({"summary": "任务执行遇到问题，请稍后重试", "task_id": self.task_id, "error": str(e)})
        finally:
            self.logger.info("Task completed, task_id=%s", self.task_id)
