"""Screen observation and one explicitly authorized Windows input operation."""

import ctypes as c
from ctypes import wintypes as w
from dataclasses import dataclass
import os
from queue import Empty, SimpleQueue
from threading import Thread
import time


class Mouse(c.Structure):
    _fields_ = [("dx", w.LONG), ("dy", w.LONG), ("data", w.DWORD),
                ("flags", w.DWORD), ("time", w.DWORD), ("extra", c.c_size_t)]


class Keyboard(c.Structure):
    _fields_ = [("key", w.WORD), ("scan", w.WORD), ("flags", w.DWORD),
                ("time", w.DWORD), ("extra", c.c_size_t)]


class InputData(c.Union):
    _fields_ = [("mouse", Mouse), ("key", Keyboard)]


class Input(c.Structure):
    _fields_ = [("type", w.DWORD), ("data", InputData)]


@dataclass(frozen=True)
class Window:
    handle: int
    pid: int
    bounds: tuple
    title: str


KEYS = {"Tab": 0x09, "Enter": 0x0D, "Escape": 0x1B, "Backspace": 0x08,
        "Delete": 0x2E, "Left": 0x25, "Up": 0x26, "Right": 0x27, "Down": 0x28,
        "Home": 0x24, "End": 0x23, "PageUp": 0x21, "PageDown": 0x22}


class Desktop:
    def __init__(self, settings):
        self.settings = settings
        self.enabled = settings["enabled"] and os.name == "nt"
        self.frame = None
        self.scope = None
        self.pending = None
        self.cancelled = False
        self.decisions = SimpleQueue()
        if self.enabled:
            self.api = c.WinDLL("user32", use_last_error=True)
            self.api.SetProcessDpiAwarenessContext(c.c_void_p(-4))
            for name, args, result in (
                ("GetForegroundWindow", [], w.HWND),
                ("GetClientRect", [w.HWND, c.POINTER(w.RECT)], w.BOOL),
                ("ClientToScreen", [w.HWND, c.POINTER(w.POINT)], w.BOOL),
                ("GetWindowThreadProcessId", [w.HWND, c.POINTER(w.DWORD)], w.DWORD),
                ("GetWindowTextW", [w.HWND, w.LPWSTR, c.c_int], c.c_int),
                ("WindowFromPoint", [w.POINT], w.HWND),
                ("GetAncestor", [w.HWND, w.UINT], w.HWND),
                ("SetCursorPos", [c.c_int, c.c_int], w.BOOL),
                ("SendInput", [w.UINT, c.POINTER(Input), c.c_int], w.UINT),
                ("MessageBoxW", [w.HWND, w.LPCWSTR, w.LPCWSTR, w.UINT], c.c_int),
            ):
                method = getattr(self.api, name)
                method.argtypes = args
                method.restype = result

    def window(self):
        handle = self.api.GetForegroundWindow()
        rect, origin, pid = w.RECT(), w.POINT(), w.DWORD()
        title = c.create_unicode_buffer(256)
        if not handle or not self.api.GetClientRect(handle, c.byref(rect)) or not self.api.ClientToScreen(handle, c.byref(origin)):
            raise ValueError("Не удалось определить активное окно")
        self.api.GetWindowThreadProcessId(handle, c.byref(pid))
        self.api.GetWindowTextW(handle, title, len(title))
        return Window(handle, pid.value, (origin.x, origin.y, origin.x + rect.right, origin.y + rect.bottom), title.value)

    def observe(self):
        if not self.enabled:
            raise ValueError("Захват рабочего стола отключён")
        from PIL import ImageGrab

        window = self.window()
        if self.scope != (window.handle, window.pid):
            raise ValueError("Выбранное окно должно быть на переднем плане")
        image = ImageGrab.grab(bbox=window.bounds, all_screens=True).convert("RGB")
        if self.window() != window:
            raise ValueError("Активное окно изменилось во время снимка")
        image.thumbnail((self.settings["max_side"], self.settings["max_side"]))
        self.frame = (window, image.size, time.monotonic())
        return image, {"title": window.title, "width": image.width, "height": image.height}

    def ready(self):
        if not self.scope:
            return False
        try:
            window = self.window()
        except ValueError:
            return False
        return (window.handle, window.pid) == self.scope

    def consent(self, title, detail, action):
        if self.pending:
            raise ValueError("Ожидается ответ в открытом окне подтверждения")
        self.pending = action
        self.cancelled = False

        def wait():
            flags = 0x00040000 | 0x00000100 | 0x00000030 | 0x00000004
            accepted = self.api.MessageBoxW(None, detail, title, flags) == 6
            if accepted:
                time.sleep(self.settings["switch_seconds"])
            self.decisions.put((action, accepted))

        Thread(target=wait, name="desktop-consent", daemon=True).start()

    def choose(self, message):
        delay = self.settings["switch_seconds"]
        self.consent(
            "Амадея — доступ к окну",
            f"Разрешить Амадее видеть одно окно в течение текущего сеанса?\n\n"
            f"Нажмите «Да» и переключитесь в нужное окно за {delay:g} с.",
            ("choose", message),
        )

    def revoke(self):
        self.scope = self.frame = None
        self.cancelled = True

    def propose(self, action):
        if not self.frame or time.monotonic() - self.frame[2] > self.settings["approval_seconds"]:
            raise ValueError("Для действия нужен свежий снимок окна")
        window, (width, height), observed = self.frame
        kind = action.get("action")
        if kind in ("click", "move"):
            x, y = action.get("x"), action.get("y")
            if type(x) is not int or type(y) is not int or not (0 <= x < width and 0 <= y < height):
                raise ValueError("Координаты должны быть внутри последнего снимка")
            data = (x, y)
        elif kind == "type":
            data = action.get("text")
            if not isinstance(data, str) or not (0 < len(data) <= 1000) or not data.isprintable():
                raise ValueError("Ввод поддерживает от 1 до 1000 печатных знаков")
        elif kind == "key" and action.get("key") in KEYS:
            data = action["key"]
        else:
            raise ValueError("Неизвестная операция рабочего стола")
        delay = self.settings["switch_seconds"]
        self.consent(
            "Амадея — подтверждение ввода",
            f"Окно {window.title!r}\nДействие {kind}: {data!r}\n\n"
            f"Чтобы выполнить действие, нажмите «Да» и вернитесь в это окно за {delay:g} с.",
            ("input", kind, data, window, (width, height), observed),
        )
        return "Предложен ввод в окне Windows."

    def poll(self):
        try:
            action, accepted = self.decisions.get_nowait()
        except Empty:
            return None
        self.pending = None
        if self.cancelled or not accepted:
            return ("notice", "Разрешение для окна отклонено.")
        if action[0] == "choose":
            target = self.window()
            self.scope = (target.handle, target.pid)
            return ("observe", action[1])
        _, kind, data, window, size, observed = action
        self.frame = None
        return ("notice", self.perform(kind, data, window, size, observed))

    def perform(self, kind, data, window, size, observed):
        if time.monotonic() - observed > self.settings["approval_seconds"]:
            raise ValueError("Срок действия снимка истёк")
        if self.scope != (window.handle, window.pid) or self.window() != window:
            raise ValueError("Окно изменилось. Нужен новый снимок")
        if kind in ("move", "click"):
            width, height = size
            left, top, right, bottom = window.bounds
            x = left + data[0] * (right - left) // width
            y = top + data[1] * (bottom - top) // height
            if self.api.GetAncestor(self.api.WindowFromPoint(w.POINT(x, y)), 2) != window.handle:
                raise ValueError("Точка перекрыта другим окном")
            if not self.api.SetCursorPos(x, y):
                raise ValueError("Не удалось переместить указатель")
            if kind == "click":
                self.send([Input(0, InputData(mouse=Mouse(flags=flag))) for flag in (2, 4)])
        elif kind == "type":
            encoded = data.encode("utf-16-le")
            units = [int.from_bytes(encoded[i:i + 2], "little") for i in range(0, len(encoded), 2)]
            self.send([Input(1, InputData(key=Keyboard(scan=unit, flags=flag)))
                       for unit in units for flag in (4, 6)])
        else:
            self.send([Input(1, InputData(key=Keyboard(key=KEYS[data], flags=flag))) for flag in (0, 2)])
        return f"Выполнено в {window.title!r}: {kind}"

    def send(self, inputs):
        batch = (Input * len(inputs))(*inputs)
        if self.api.SendInput(len(inputs), batch, c.sizeof(Input)) != len(inputs):
            raise ValueError("Windows не приняла часть событий ввода")
