import asyncio
import json
import os
import time
import urllib.error
import urllib.request

from mcp import types
from pydantic import Field

from mcp_server.common.errors import handle_error
from mcp_server.tools import MCP


def _planner_endpoint() -> str:
    return os.getenv("PLANNER_ENDPOINT", "http://127.0.0.1:38189").rstrip("/")


def _run_planner_task(
    *,
    instruction: str,
    model_name: str,
    system_prompt: str,
    timeout_seconds: float,
) -> dict:
    endpoint = _planner_endpoint()
    payload = json.dumps(
        {
            "model_name": model_name,
            "user_prompt": instruction,
            "system_prompt": system_prompt,
        },
        ensure_ascii=False,
    ).encode("utf-8")
    request = urllib.request.Request(
        f"{endpoint}/run/task",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    started = time.monotonic()
    events: list[dict] = []
    screenshots_seen = 0
    actions: list[str] = []
    task_id = None
    trace: list[dict] = []

    with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
        for raw_line in response:
            if time.monotonic() - started > timeout_seconds:
                return {
                    "status": "timeout",
                    "agent_endpoint": endpoint,
                    "model_name": model_name,
                    "elapsed": round(time.monotonic() - started, 3),
                    "screenshots_seen": screenshots_seen,
                    "actions": actions,
                    "agent_task": {
                        "instruction": instruction,
                        "model_name": model_name,
                        "system_prompt": system_prompt,
                    },
                    "trace": trace,
                    "events": events,
                }

            line = raw_line.decode("utf-8", errors="replace").strip()
            if not line.startswith("data:"):
                continue
            data = json.loads(line[5:].strip())
            task_id = task_id or data.get("task_id")
            if "screenshot" in data:
                screenshots_seen += 1
                event = {
                    "event": "screenshot",
                    "task_id": data.get("task_id"),
                    "step": data.get("step"),
                    "screen": data.get("screen"),
                    "screenshot_omitted": True,
                }
                events.append(event)
                trace.append(event)
                continue

            event: dict = {"task_id": data.get("task_id")}
            for key in (
                "event",
                "step",
                "screen",
                "action",
                "summary",
                "error",
                "model_response",
                "parsed_summary",
                "parsed_action",
                "tool_name",
                "tool_arguments",
                "coordinate_transform",
                "overlay_result",
                "tool_result",
            ):
                if key in data:
                    event[key] = data[key]
            if "action" in event:
                actions.append(str(event["action"]))
            events.append(event)
            trace.append(event)
            action = str(event.get("action", ""))
            if action.startswith("完成") or event.get("error"):
                break

    return {
        "status": "ok",
        "agent_endpoint": endpoint,
        "model_name": model_name,
        "elapsed": round(time.monotonic() - started, 3),
        "task_id": task_id,
        "screenshots_seen": screenshots_seen,
        "actions": actions,
        "agent_task": {
            "instruction": instruction,
            "model_name": model_name,
            "system_prompt": system_prompt,
        },
        "trace": trace,
        "events": events,
    }


@MCP.tool(
    name="run_vlm_agent_task",
    description=(
        "Run a GUI task through the Doubao VLM agent loop. Doubao performs visual grounding, "
        "thought/action selection, and step-by-step GUI control from screenshots."
    ),
)
async def run_vlm_agent_task(
    instruction: str = Field(description="User GUI task instruction for Doubao VLM"),
    model_name: str = Field(default="doubao-seed-2-0-lite-260428"),
    system_prompt: str = Field(default=""),
    timeout_seconds: float = Field(default=60.0, ge=1.0, le=300.0),
):
    try:
        result = await asyncio.to_thread(
            _run_planner_task,
            instruction=instruction,
            model_name=model_name,
            system_prompt=system_prompt,
            timeout_seconds=timeout_seconds,
        )
        return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]
    except urllib.error.HTTPError as e:
        return handle_error("run_vlm_agent_task", RuntimeError(e.read().decode("utf-8", errors="replace")))
    except Exception as e:
        return handle_error("run_vlm_agent_task", e)
