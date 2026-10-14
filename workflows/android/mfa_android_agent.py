"""python-for-android service entrypoint used by the MFA resource workflow example.

The Android host passes a JSON object through PYTHON_SERVICE_ARGUMENT. Resource projects
may replace this adapter with their own service entrypoint when their Agent needs custom
initialization. The desktop Agent command in interface.json is left unchanged.
"""

from __future__ import annotations

import json
import os
import platform
import runpy
import sys
import threading
import traceback
from pathlib import Path
from typing import Any


_FATAL_PREFIX = "__MFA_ANDROID_AGENT_FATAL__:"
_EXIT_PREFIX = "__MFA_ANDROID_AGENT_EXIT__:"


def _service_argument() -> dict[str, Any]:
    raw = os.environ.get("PYTHON_SERVICE_ARGUMENT", "")
    if not raw:
        raise RuntimeError("PYTHON_SERVICE_ARGUMENT is empty")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise TypeError("PYTHON_SERVICE_ARGUMENT must contain a JSON object")
    return value


def _send_terminal_event_direct(config: dict[str, Any], line: str) -> None:
    """Send startup failure even if constructing the normal output bridge failed."""
    if os.environ.get("MFA_ANDROID_OUTPUT_BRIDGED") == "1":
        return
    action = str(config.get("output_action") or "")
    if not action:
        return
    try:
        from jnius import autoclass

        intent_type = autoclass("android.content.Intent")
        string_type = autoclass("java.lang.String")
        service_type = autoclass("org.kivy.android.PythonService")
        context = service_type.mService
        if context is None:
            return
        intent = intent_type(action)
        package = str(config.get("output_package") or "")
        if package:
            intent.setPackage(package)
        intent.putExtra("line", string_type(line))
        context.sendBroadcast(intent)
    except BaseException:
        # Preserve the original resource-Agent exception. Android logcat remains the
        # final fallback when even JNI/bootstrap initialization is unavailable.
        pass


def _unquote(value: str) -> str:
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
        return value[1:-1]
    return value


def _resolve_script(config: dict[str, Any], root: Path) -> tuple[Path, list[str]]:
    arguments = [_unquote(str(item)) for item in config.get("child_args") or []]
    configured = str(config.get("entrypoint") or "").strip()
    program = _unquote(str(config.get("child_exec") or "").strip())

    if configured:
        script = configured
        trailing_arguments: list[str] = []
    elif program.lower().endswith((".py", ".pyc")):
        script = program
        trailing_arguments = arguments
    else:
        script_index = next(
            (index for index in range(len(arguments) - 1, -1, -1)
             if arguments[index].lower().endswith((".py", ".pyc"))),
            -1,
        )
        if script_index < 0:
            raise RuntimeError(
                "No Python script was found in interface agent.child_exec/child_args. "
                "Set the MFA_ANDROID_PYTHON_ENTRYPOINT repository variable."
            )
        script = arguments[script_index]
        trailing_arguments = arguments[script_index + 1:]

    script_path = Path(script)
    if not script_path.is_absolute():
        script_path = root / script_path
    script_path = script_path.resolve()
    if not script_path.is_file():
        raise FileNotFoundError(f"Android Agent entrypoint does not exist: {script_path}")
    return script_path, trailing_arguments


class _AndroidOutputBridge:
    """Tee complete output lines to MFA's main Android process."""

    def __init__(self, target: Any, config: dict[str, Any]) -> None:
        self._target = target
        self._buffer = ""
        self._lock = threading.Lock()
        self._action = str(config.get("output_action") or "")
        self._package = str(config.get("output_package") or "")

        from jnius import autoclass

        self._intent_type = autoclass("android.content.Intent")
        self._string_type = autoclass("java.lang.String")
        service_type = autoclass("org.kivy.android.PythonService")
        self._context = service_type.mService

    def write(self, value: object) -> int:
        text = str(value)
        written = self._target.write(text)
        with self._lock:
            self._buffer += text
            while "\n" in self._buffer:
                line, self._buffer = self._buffer.split("\n", 1)
                self._send(line.rstrip("\r"))
        return written if isinstance(written, int) else len(text)

    def flush(self) -> None:
        self._target.flush()
        with self._lock:
            if self._buffer:
                self._send(self._buffer.rstrip("\r"))
                self._buffer = ""

    def isatty(self) -> bool:
        return False

    @property
    def encoding(self) -> str:
        return getattr(self._target, "encoding", "utf-8")

    @property
    def errors(self) -> str:
        return getattr(self._target, "errors", "strict")

    def reconfigure(self, **kwargs: Any) -> None:
        """Match TextIOWrapper sufficiently for resource Agent bootstrap scripts.

        python-for-android initially exposes a LogFile object as stdout/stderr.  A
        number of desktop-oriented Agent entrypoints inspect ``encoding`` and call
        ``reconfigure`` before starting Maa's AgentServer, so the Android adapter must
        provide those members even when the underlying LogFile does not.
        """
        reconfigure = getattr(self._target, "reconfigure", None)
        if callable(reconfigure):
            reconfigure(**kwargs)

    def fileno(self) -> int:
        fileno = getattr(self._target, "fileno", None)
        return fileno() if callable(fileno) else -1

    def _send(self, line: str) -> None:
        if not line or not self._action or self._context is None:
            return
        intent = self._intent_type(self._action)
        if self._package:
            intent.setPackage(self._package)
        intent.putExtra("line", self._string_type(line))
        self._context.sendBroadcast(intent)


def _install_output_bridge(config: dict[str, Any]) -> None:
    # Install this unconditionally.  Besides forwarding lines to MFA, it is the
    # compatibility TextIO layer presented to resource-provided Agent scripts.
    # Keeping it active when output broadcasting is unavailable prevents p4a's
    # minimal LogFile stream from crashing otherwise valid desktop entrypoints.
    sys.stdout = _AndroidOutputBridge(sys.stdout, config)
    sys.stderr = _AndroidOutputBridge(sys.stderr, config)
    os.environ["MFA_ANDROID_OUTPUT_BRIDGED"] = "1"


def _preload_maa_agent_binding() -> None:
    """Load Maa's Agent binding with Android mapped to its Linux-style .so names.

    MaaFramework's Python package up to 5.12.3 indexes its native-library table with
    ``platform.system().lower()`` but only defines windows/darwin/linux entries.  CPython
    built by python-for-android reports ``Android``, so importing ``maa`` otherwise fails
    with ``KeyError: 'android'`` before AgentServer can connect.

    Android MaaFramework artifacts use the same ``lib*.so`` names as Linux.  Limit the
    compatibility alias to the binding import so resource Agent code can still correctly
    detect Android afterwards.
    """

    if platform.system().lower() != "android":
        return

    original_system = platform.system
    platform.system = lambda: "Linux"
    try:
        # Importing the Agent module initializes Library in agent_server mode and caches
        # Maa's ctypes declarations before the resource entrypoint imports them again.
        from maa.agent.agent_server import AgentServer  # noqa: F401
    finally:
        platform.system = original_system


def _run_resource_agent(config: dict[str, Any]) -> None:
    _install_output_bridge(config)
    root = Path(str(config["data_root"])).resolve()
    if not root.is_dir():
        raise FileNotFoundError(f"MFA data root does not exist: {root}")

    for path in (root, root / "agent", root / "python"):
        path_text = str(path)
        if path.exists() and path_text not in sys.path:
            sys.path.insert(0, path_text)

    native_library_dir = str(config.get("native_library_dir") or "")
    if native_library_dir:
        os.environ["MAA_LIBRARY_DIR"] = native_library_dir
        os.environ["MAA_FRAMEWORK_LIB_DIR"] = native_library_dir
        old_library_path = os.environ.get("LD_LIBRARY_PATH", "")
        os.environ["LD_LIBRARY_PATH"] = (
            native_library_dir
            if not old_library_path
            else native_library_dir + os.pathsep + old_library_path
        )

    _preload_maa_agent_binding()

    os.environ["MFA_INSTANCE_ID"] = str(config.get("instance_id") or "")
    os.environ["MFA_INSTANCE_NAME"] = str(config.get("instance_name") or "")

    script, trailing_arguments = _resolve_script(config, root)
    client_id = str(config["client_id"])
    os.chdir(root)
    sys.argv = [str(script), *trailing_arguments, client_id]
    runpy.run_path(str(script), run_name="__main__")


def main() -> None:
    config = _service_argument()
    try:
        _run_resource_agent(config)
    except BaseException as error:
        # A p4a service is a separate Android process, so its exit code cannot be
        # observed like a desktop child Process.  Report a machine-readable terminal
        # event after the normal traceback; MFA uses it to fail Agent startup at once.
        traceback.print_exc()
        fatal_line = f"{_FATAL_PREFIX}{type(error).__name__}: {error}"
        _send_terminal_event_direct(config, fatal_line)
        print(
            fatal_line,
            file=sys.stderr,
            flush=True,
        )
        raise
    else:
        # Returning before the Maa Agent handshake is equivalent to a desktop child
        # process exiting with no usable Agent server.
        print(
            f"{_EXIT_PREFIX}resource Agent entrypoint returned",
            file=sys.stderr,
            flush=True,
        )


if __name__ == "__main__":
    main()
