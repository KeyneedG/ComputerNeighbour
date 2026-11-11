from datetime import datetime
import math
import time


def clamp(value):
    return max(0.0, min(1.0, value))


class Physiology:
    def __init__(self, store, settings):
        self.store = store
        self.cycle = settings["cycle"]
        self.hormones = settings["physiology"]
        self.mood = settings["mood"]
        self.state = store.get("physiology", {
            "at": time.time(), "sleeping": False, "pressure": self.cycle["wake_at"],
            "hormones": {name: value["baseline"] for name, value in self.hormones.items()},
        })
        self.tick()

    def tick(self, signals=None, wake=False):
        now = time.time()
        elapsed = max(0, now - self.state["at"])
        awake_span = self.cycle["awake_hours"] * 3600
        sleep_span = self.cycle["sleep_hours"] * 3600
        distance = self.cycle["sleep_at"] - self.cycle["wake_at"]
        sleeping = self.state["sleeping"]
        pressure = self.state["pressure"]
        # Integrate through transitions as if the timer had kept running.
        while elapsed > 0:
            rate = -distance / sleep_span if sleeping else distance / awake_span
            threshold = self.cycle["wake_at" if sleeping else "sleep_at"]
            until = max(0, (threshold - pressure) / rate)
            if elapsed < until:
                pressure = clamp(pressure + elapsed * rate)
                break
            elapsed = (elapsed - until) % (awake_span + sleep_span)
            pressure = threshold
            sleeping = not sleeping
        if wake:
            sleeping = False
        elapsed = max(0, now - self.state["at"])
        local = datetime.now()
        circadian = (1 + math.cos((local.hour + local.minute / 60 - 3) * math.tau / 24)) / 2
        inputs = {"rest": float(sleeping), **(signals or {})}
        inputs.pop("circadian", None)
        levels = {}
        for name, rule in self.hormones.items():
            baseline = clamp(rule["baseline"] + rule.get("circadian", 0) * circadian)
            decay = 2 ** (-elapsed / (60 * rule["half_life_minutes"]))
            old = self.state["hormones"].get(name, baseline)
            impulse = sum(rule.get(key, 0) * amount for key, amount in inputs.items())
            if sleeping:
                impulse -= rule.get("rest", 0) * (1 - min(1.0, elapsed / 300))
            levels[name] = clamp(baseline + (old - baseline) * decay + impulse)
        self.state = {"at": now, "sleeping": sleeping, "pressure": pressure, "hormones": levels}
        self.store.set("physiology", self.state)
        return self.view()

    def view(self):
        levels = self.state["hormones"]
        return {
            "local_time": datetime.now().isoformat(timespec="minutes"),
            "sleeping": self.state["sleeping"],
            "fatigue": round(self.state["pressure"], 3),
            "hormones": {key: round(value, 3) for key, value in levels.items()},
            "mood": {name: round(clamp(rule.get("bias", 0) + sum(
                rule.get(hormone, 0) * amount for hormone, amount in levels.items()
            )), 3) for name, rule in self.mood.items()},
        }
