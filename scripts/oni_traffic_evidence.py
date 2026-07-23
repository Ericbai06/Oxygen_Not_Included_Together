import time
import math


TRAFFIC_KEYS = (
    "motionCalls",
    "motionBytes",
    "animationCalls",
    "animationBytes",
    "cursorCalls",
    "cursorBytes",
)

COMMAND_KEYS = ("txCalls", "txBytes", "txFailures")


def _counter(status, key):
    value = int(status.get(key, 0))
    if value < 0:
        raise ValueError(f"negative traffic counter: {key}={value}")
    return value


def _endpoint_delta(first, last):
    result = {}
    for key in TRAFFIC_KEYS:
        start = _counter(first, key)
        end = _counter(last, key)
        if end < start:
            raise ValueError(f"traffic counter reset inside sample window: {key}")
        result[key] = end - start
    return result


def window_totals(evidence):
    samples = evidence.get("samples", [])
    if len(samples) < 2:
        raise ValueError("traffic evidence requires at least two samples")
    totals = {key: 0 for key in TRAFFIC_KEYS}
    for role in ("host", "client"):
        delta = _endpoint_delta(samples[0][role], samples[-1][role])
        for key in TRAFFIC_KEYS:
            totals[key] += delta[key]
    return totals


def compare_totals(before, after, minimum_reduction_percent=60.0):
    metrics = {}
    failures = []
    for key in TRAFFIC_KEYS:
        baseline = int(before.get(key, 0))
        current = int(after.get(key, 0))
        reduction = None if baseline <= 0 else (baseline - current) * 100.0 / baseline
        passed = reduction is not None and reduction + 1e-9 >= minimum_reduction_percent
        metrics[key] = {
            "before": baseline,
            "after": current,
            "reductionPercent": reduction,
            "passed": passed,
        }
        if not passed:
            failures.append(key)
    return {"passed": not failures, "failures": failures, "metrics": metrics}


def compare_evidence(before, after, minimum_reduction_percent=60.0):
    return compare_totals(
        window_totals(before),
        window_totals(after),
        minimum_reduction_percent,
    )


def command_window(before, after):
    result = {}
    for key in COMMAND_KEYS:
        start = _counter(before, key)
        end = _counter(after, key)
        if end < start:
            raise ValueError(f"native counter reset during command: {key}")
        result[key] = end - start
    return result


def compare_replay_windows(before, after, minimum_reduction_percent=50.0):
    baseline = int(before.get("txCalls", 0))
    current = int(after.get("txCalls", 0))
    reduction = None if baseline <= 0 else (baseline - current) * 100.0 / baseline
    passed = reduction is not None and reduction + 1e-9 >= minimum_reduction_percent
    return {
        "passed": passed,
        "beforeTxCalls": baseline,
        "afterTxCalls": current,
        "reductionPercent": reduction,
    }


def _percentile(values, percentile):
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, math.ceil(percentile * len(ordered)) - 1)
    return ordered[index]


def acceptance_health(evidence):
    samples = evidence.get("samples", [])
    if len(samples) < 2:
        raise ValueError("health evidence requires at least two samples")
    queue_usec = []
    unacked_bytes = []
    tx_failures = 0
    for role in ("host", "client"):
        start = int(samples[0][role].get("txFailures", 0))
        end = int(samples[-1][role].get("txFailures", 0))
        tx_failures += max(0, end - start)
    for sample in samples:
        for role in ("host", "client"):
            status = sample[role]
            queue = int(status.get("steamQueueUsec", -1))
            unacked = int(status.get("steamUnackedReliableBytes", -1))
            if queue >= 0:
                queue_usec.append(queue)
            if unacked >= 0:
                unacked_bytes.append(unacked)
    queue_max_ms = max(queue_usec) / 1000.0 if queue_usec else None
    queue_p95_ms = _percentile(queue_usec, 0.95)
    queue_p95_ms = None if queue_p95_ms is None else queue_p95_ms / 1000.0
    unacked_max = max(unacked_bytes) if unacked_bytes else None
    unacked_p95 = _percentile(unacked_bytes, 0.95)
    failures = []
    if tx_failures != 0:
        failures.append("txFailures")
    if queue_max_ms is None or queue_max_ms >= 1000:
        failures.append("steamQueueMaxMs")
    if queue_p95_ms is None or queue_p95_ms >= 500:
        failures.append("steamQueueP95Ms")
    if unacked_max is None or unacked_max >= 64 * 1024:
        failures.append("steamUnackedReliableMaxBytes")
    if unacked_p95 is None or unacked_p95 >= 16 * 1024:
        failures.append("steamUnackedReliableP95Bytes")
    return {
        "passed": not failures,
        "failures": failures,
        "txFailures": tx_failures,
        "steamQueueMaxMs": queue_max_ms,
        "steamQueueP95Ms": queue_p95_ms,
        "steamUnackedReliableMaxBytes": unacked_max,
        "steamUnackedReliableP95Bytes": unacked_p95,
    }


def sample_status(get_status, host, client, duration, interval=1.0):
    if duration <= 0 or interval <= 0:
        raise ValueError("duration and interval must be positive")
    started = time.monotonic()
    samples = []
    while True:
        now = time.monotonic()
        samples.append({
            "elapsedSeconds": now - started,
            "unixSeconds": time.time(),
            "host": get_status(host),
            "client": get_status(client),
        })
        remaining = duration - (time.monotonic() - started)
        if remaining <= 0:
            break
        time.sleep(min(interval, remaining))
    return {"durationSeconds": duration, "intervalSeconds": interval, "samples": samples}
