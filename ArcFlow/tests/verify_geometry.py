#!/usr/bin/env python3
"""Independent openNURBS check for ArcFlow arc-join control-point continuity.

Requires rhino3dm 8.32.0 or newer. The script mirrors ArcFlow's tangent-arc and
biarc construction equations, converts every result to its rational quadratic
NURBS form, and measures the two control points adjacent to each join.
"""

import math
import random
import sys
from pathlib import Path

import rhino3dm


EPSILON = 1e-12


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def mul(a, scale):
    return tuple(x * scale for x in a)


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def cross_length(a, b):
    x = a[1] * b[2] - a[2] * b[1]
    y = a[2] * b[0] - a[0] * b[2]
    z = a[0] * b[1] - a[1] * b[0]
    return math.sqrt(x * x + y * y + z * z)


def length(a):
    return math.sqrt(dot(a, a))


def unit(a):
    value = length(a)
    if value <= EPSILON:
        raise ValueError("zero vector")
    return mul(a, 1.0 / value)


def point(value):
    return rhino3dm.Point3d(*value)


def tuple3(value):
    return value.X, value.Y, value.Z


def rotate_xy(vector, angle):
    cosine = math.cos(angle)
    sine = math.sin(angle)
    return (
        vector[0] * cosine - vector[1] * sine,
        vector[0] * sine + vector[1] * cosine,
        vector[2],
    )


def tangent_arc(start, tangent, end):
    tangent = unit(tangent)
    chord = sub(end, start)
    left = (-tangent[1], tangent[0], 0.0)
    denominator = 2.0 * dot(chord, left)
    if abs(denominator) <= EPSILON:
        return None

    signed_radius = dot(chord, chord) / denominator
    center = add(start, mul(left, signed_radius))
    radial = sub(start, center)
    start_angle = math.atan2(radial[1], radial[0])
    end_radial = sub(end, center)
    end_angle = math.atan2(end_radial[1], end_radial[0])
    direction = 1.0 if radial[0] * tangent[1] - radial[1] * tangent[0] >= 0.0 else -1.0
    if direction > 0.0:
        sweep = (end_angle - start_angle) % (2.0 * math.pi)
    else:
        sweep = -((start_angle - end_angle) % (2.0 * math.pi))
    if abs(sweep) <= EPSILON:
        return None

    middle = add(center, rotate_xy(radial, sweep * 0.5))
    arc = rhino3dm.Arc(point(start), point(middle), point(end))
    return arc if arc.IsValid else None


def biarc(start, start_tangent, end, end_tangent):
    t0 = unit(start_tangent)
    t1 = unit(end_tangent)
    chord = sub(end, start)
    tangent_sum = add(t0, t1)
    a = 2.0 * (1.0 - dot(t0, t1))
    b = 2.0 * dot(chord, tangent_sum)
    c = -dot(chord, chord)

    if abs(a) <= EPSILON:
        if abs(b) <= EPSILON:
            return None
        distance = -c / b
    else:
        discriminant = b * b - 4.0 * a * c
        if discriminant < 0.0:
            return None
        root = math.sqrt(discriminant)
        roots = [(-b + root) / (2.0 * a), (-b - root) / (2.0 * a)]
        positive = [value for value in roots if value > EPSILON]
        if not positive:
            return None
        distance = min(positive)

    if distance <= EPSILON:
        return None
    join = mul(add(add(start, end), mul(sub(t0, t1), distance)), 0.5)
    first = tangent_arc(start, t0, join)
    reverse_second = tangent_arc(end, mul(t1, -1.0), join)
    if first is None or reverse_second is None:
        return None
    reverse_second.Reverse()
    return first, reverse_second


def euclidean_control(nurbs, index):
    control = nurbs.Points[index]
    return control.X / control.W, control.Y / control.W, control.Z / control.W


def join_metrics(first, second):
    first_nurbs = first.ToNurbsCurve()
    second_nurbs = second.ToNurbsCurve()
    incoming_control = euclidean_control(first_nurbs, len(first_nurbs.Points) - 2)
    outgoing_control = euclidean_control(second_nurbs, 1)
    join = tuple3(first.EndPoint)
    incoming = sub(join, incoming_control)
    outgoing = sub(outgoing_control, join)
    cross = cross_length(incoming, outgoing)
    return {
        "gap": length(sub(tuple3(first.EndPoint), tuple3(second.StartPoint))),
        "angle": math.atan2(cross, dot(incoming, outgoing)),
        "line": cross / length(incoming),
        "between": dot(incoming, outgoing) >= 0.0,
    }


def append_g1(arcs, candidate):
    if candidate is None:
        return False
    if arcs:
        metrics = join_metrics(arcs[-1], candidate)
        if (
            metrics["gap"] > 1e-8
            or metrics["angle"] > 1e-7
            or metrics["line"] > 1e-8
            or not metrics["between"]
        ):
            return False
    arcs.append(candidate)
    return True


def quarter_chain(start_radius, turns, golden):
    arcs = []
    current = (start_radius, 0.0, 0.0)
    tangent = (0.0, 1.0, 0.0)
    fib_a = 1.0
    fib_b = 1.0
    count = max(1, math.ceil(turns * 4.0))
    for index in range(count):
        if golden:
            scale = math.pow((1.0 + math.sqrt(5.0)) * 0.5, index)
        else:
            scale = fib_a
            fib_a, fib_b = fib_b, fib_a + fib_b
        radius = start_radius * scale
        left = (-tangent[1], tangent[0], 0.0)
        center = add(current, mul(left, radius))
        radial = unit(sub(current, center))
        sweep = min(1.0, turns * 4.0 - index) * math.pi * 0.5
        middle = add(center, mul(rotate_xy(radial, sweep * 0.5), radius))
        end = add(center, mul(rotate_xy(radial, sweep), radius))
        arc = rhino3dm.Arc(point(current), point(middle), point(end))
        if not append_g1(arcs, arc):
            break
        current = tuple3(arc.EndPoint)
        tangent = unit(tuple3(arc.TangentAt(arc.AngleDomain.T1)))
    return arcs


def analytic_chain(kind, start_radius, turns, growth, count=96):
    samples = []
    total_angle = turns * math.pi * 2.0
    for index in range(count + 1):
        u = total_angle * index / count
        if kind == "archimedean":
            derivative = growth / (2.0 * math.pi)
            radius = start_radius + derivative * u
        elif kind == "logarithmic":
            exponent = math.log(max(growth, 1e-6)) / (2.0 * math.pi)
            radius = start_radius * math.exp(exponent * u)
            derivative = exponent * radius
        else:
            end_radius = max(start_radius + growth, start_radius * 1.01)
            coefficient = (end_radius * end_radius - start_radius * start_radius) / (2.0 * math.pi)
            radius = math.sqrt(max(start_radius * start_radius + coefficient * u, EPSILON))
            derivative = coefficient / (2.0 * radius)
        cosine = math.cos(u)
        sine = math.sin(u)
        position = (radius * cosine, radius * sine, 0.0)
        tangent = unit((derivative * cosine - radius * sine, derivative * sine + radius * cosine, 0.0))
        samples.append((position, tangent))

    arcs = []
    current_tangent = samples[0][1]
    for index in range(len(samples) - 1):
        start = samples[index][0]
        end = samples[index + 1][0]
        pair = biarc(start, current_tangent, end, samples[index + 1][1])
        if pair is not None:
            if not append_g1(arcs, pair[0]) or not append_g1(arcs, pair[1]):
                break
            current_tangent = unit(tuple3(pair[1].TangentAt(pair[1].AngleDomain.T1)))
            continue
        fallback = tangent_arc(start, current_tangent, end)
        if not append_g1(arcs, fallback):
            break
        current_tangent = unit(tuple3(fallback.TangentAt(fallback.AngleDomain.T1)))
    return arcs


def validate(name, arcs, totals):
    if len(arcs) < 2:
        raise AssertionError(f"{name}: only {len(arcs)} arc(s)")
    for index in range(len(arcs) - 1):
        metrics = join_metrics(arcs[index], arcs[index + 1])
        totals["joins"] += 1
        totals["gap"] = max(totals["gap"], metrics["gap"])
        totals["angle"] = max(totals["angle"], metrics["angle"])
        totals["line"] = max(totals["line"], metrics["line"])
        if (
            metrics["gap"] > 1e-8
            or metrics["angle"] > 1e-7
            or metrics["line"] > 1e-8
            or not metrics["between"]
        ):
            raise AssertionError(f"{name} join {index}: {metrics}")


def main():
    root = Path(__file__).resolve().parents[1]
    project = (root / "src" / "ArcFlow" / "ArcFlow.csproj").read_text(encoding="utf-8")
    installer = (root / "install.ps1").read_text(encoding="utf-8")
    build = (root / "build.ps1").read_text(encoding="utf-8")
    assert "<TargetFrameworks>net48;net8.0</TargetFrameworks>" in project
    assert '<PackageReference Include="RhinoCommon" Version="7.0.20314.3001"' in project
    assert "<Prefer32Bit>false</Prefer32Bit>" in project
    assert "<Version>1.2.1</Version>" in project
    for token in ['ValidateSet("Auto", "7", "8", "Both")', 'Plug-ins\\{$pluginId}', "Unblock-File", '"LoadMode"']:
        assert token in installer, token
    for package in [
        "ArcFlow-1.2.1-rhino7.zip",
        "ArcFlow-1.2.1-rhino8.zip",
        "ArcFlow-1.2.1-rhino7-rhino8.zip",
    ]:
        assert package in build, package

    totals = {"joins": 0, "gap": 0.0, "angle": 0.0, "line": 0.0}
    chains = {
        "fibonacci": quarter_chain(10.0, 2.0, False),
        "golden": quarter_chain(10.0, 2.0, True),
        "archimedean": analytic_chain("archimedean", 10.0, 2.0, 20.0),
        "logarithmic": analytic_chain("logarithmic", 10.0, 2.0, 2.0),
        "fermat": analytic_chain("fermat", 10.0, 2.0, 20.0),
    }
    for name, arcs in chains.items():
        validate(name, arcs, totals)

    random_source = random.Random(1202026)
    solved = 0
    for _ in range(500):
        end = (2.0 + random_source.random() * 20.0, -8.0 + random_source.random() * 16.0, 0.0)
        start_angle = -1.2 + random_source.random() * 2.4
        end_angle = -1.2 + random_source.random() * 2.4
        pair = biarc(
            (0.0, 0.0, 0.0),
            (math.cos(start_angle), math.sin(start_angle), 0.0),
            end,
            (math.cos(end_angle), math.sin(end_angle), 0.0),
        )
        if pair is None:
            continue
        validate("random-biarc", list(pair), totals)
        solved += 1

    if solved < 100:
        raise AssertionError(f"random coverage too low: {solved}")
    print(f"PASS joins={totals['joins']} random_biarcs={solved}")
    print(f"max_gap={totals['gap']:.12e}")
    print(f"max_control_angle={totals['angle']:.12e} rad")
    print(f"max_control_line_deviation={totals['line']:.12e}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
