import ctypes
import os
from functools import lru_cache


DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ctypes.c_void_p(-4)


@lru_cache(maxsize=1)
def enable_dpi_awareness() -> dict[str, object]:
    if os.name != "nt":
        return {"enabled": False, "platform": os.name, "method": None}

    user32 = ctypes.windll.user32
    try:
        ok = user32.SetProcessDpiAwarenessContext(
            DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        )
        if ok:
            return {"enabled": True, "platform": "nt", "method": "per_monitor_v2"}
    except (AttributeError, OSError):
        pass

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        return {"enabled": True, "platform": "nt", "method": "per_monitor"}
    except (AttributeError, OSError):
        pass

    try:
        ok = user32.SetProcessDPIAware()
        if ok:
            return {"enabled": True, "platform": "nt", "method": "system"}
    except (AttributeError, OSError):
        pass

    return {"enabled": False, "platform": "nt", "method": None}


def get_process_dpi_awareness() -> int | None:
    if os.name != "nt":
        return None
    try:
        awareness = ctypes.c_int()
        ctypes.windll.shcore.GetProcessDpiAwareness(0, ctypes.byref(awareness))
        return int(awareness.value)
    except (AttributeError, OSError):
        return None
