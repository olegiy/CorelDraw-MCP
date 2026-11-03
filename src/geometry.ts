import { AutomationFailure } from './types.js';

export interface Point {
  x: number;
  y: number;
}
export type Segment =
  | { kind: 'line'; p0: Point; p1: Point }
  | { kind: 'cubic'; p0: Point; p1: Point; p2: Point; p3: Point };
export interface GeometrySnapshot {
  objects: Array<{ static_id: number; segments: Segment[] }>;
}
export interface Measurements {
  min_width_mm: number;
  /** Направление параллельных опорных прямых, в градусах [0,180). */
  min_width_angle_deg: number;
  rectangle: { width_mm: number; height_mm: number; angle_deg: number; area_mm2: number; corners: Point[] };
}

const LIMIT = 200000;
const fail = (code: string, message: string): never => {
  throw new AutomationFailure(code, message);
};
function invalid(message: string): never {
  return fail('GEOMETRY_INVALID_INPUT', message);
}
const same = (a: Point, b: Point) => a.x === b.x && a.y === b.y;
function exact(value: unknown, keys: string[]): value is Record<string, unknown> {
  return (
    value !== null &&
    typeof value === 'object' &&
    !Array.isArray(value) &&
    Object.keys(value).length === keys.length &&
    keys.every((k) => Object.hasOwn(value, k))
  );
}
function validate(snapshot: unknown, tol: number): { snapshot: GeometrySnapshot; roundoff: number } {
  if (!Number.isFinite(tol) || tol < 0.001 || tol > 1)
    invalid('tolerance_mm должен быть в диапазоне 0.001..1');
  if (!exact(snapshot, ['objects']) || !Array.isArray(snapshot.objects) || !snapshot.objects.length)
    invalid('Ожидается непустой список объектов геометрии');
  if (snapshot.objects.length > 100) fail('GEOMETRY_LIMIT_EXCEEDED', 'Допускается не более 100 объектов');
  const ids = new Set<number>();
  let segments = 0,
    maxAbs = 1;
  for (const object of snapshot.objects) {
    if (
      !exact(object, ['static_id', 'segments']) ||
      !Number.isSafeInteger(object.static_id) ||
      (object.static_id as number) <= 0 ||
      !Array.isArray(object.segments) ||
      !object.segments.length
    )
      invalid('Неверный объект геометрии');
    if (ids.has(object.static_id as number)) invalid('Повтор static_id в геометрии');
    ids.add(object.static_id as number);
    for (const segment of object.segments) {
      const kind = (segment as Record<string, unknown> | null)?.kind;
      const names = kind === 'line' ? ['p0', 'p1'] : kind === 'cubic' ? ['p0', 'p1', 'p2', 'p3'] : [];
      if (!names.length || !exact(segment, ['kind', ...names])) invalid('Неверный сегмент геометрии');
      if (++segments > LIMIT) fail('GEOMETRY_LIMIT_EXCEEDED', 'Превышен лимит 200000 исходных сегментов');
      for (const name of names) {
        const p = segment[name];
        if (
          !exact(p, ['x', 'y']) ||
          typeof p.x !== 'number' ||
          typeof p.y !== 'number' ||
          !Number.isFinite(p.x) ||
          !Number.isFinite(p.y)
        )
          invalid('Координаты должны быть конечными числами');
        maxAbs = Math.max(maxAbs, Math.abs(p.x), Math.abs(p.y));
      }
    }
  }
  // Reserve numerical error before choosing the subdivision threshold. This also
  // bounds all intermediate squares/products and refuses unresolvable offsets.
  const roundoff = 4096 * Number.EPSILON * maxAbs;
  if (maxAbs > 1e12 || roundoff > tol / 8)
    fail('GEOMETRY_NUMERIC_LIMIT', 'Координаты слишком велики для заданной точности');
  return { snapshot: snapshot as unknown as GeometrySnapshot, roundoff };
}

function distanceToSegment(p: Point, a: Point, b: Point): number {
  const dx = b.x - a.x,
    dy = b.y - a.y,
    den = dx * dx + dy * dy;
  const t = den ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / den)) : 0;
  return Math.hypot(p.x - a.x - t * dx, p.y - a.y - t * dy);
}
const middle = (a: Point, b: Point): Point => ({ x: a.x + (b.x - a.x) / 2, y: a.y + (b.y - a.y) / 2 });
function flatten(segment: Extract<Segment, { kind: 'cubic' }>, tol: number, emit: (p: Point) => void): void {
  const visit = (a: Point, b: Point, c: Point, d: Point, depth: number): void => {
    // Convexity of distance to a convex chord segment bounds the whole Bezier.
    // Conversely, continuity of the projection covers every point on the chord.
    if (Math.max(distanceToSegment(b, a, d), distanceToSegment(c, a, d)) <= tol) {
      emit(d);
      return;
    }
    if (depth >= 60) fail('GEOMETRY_NUMERIC_LIMIT', 'Недостаточно численной точности для аппроксимации');
    const ab = middle(a, b),
      bc = middle(b, c),
      cd = middle(c, d),
      abc = middle(ab, bc),
      bcd = middle(bc, cd),
      m = middle(abc, bcd);
    visit(a, ab, abc, m, depth + 1);
    visit(m, bcd, cd, d, depth + 1);
  };
  visit(segment.p0, segment.p1, segment.p2, segment.p3, 0);
}
function cross(a: Point, b: Point, c: Point): number {
  return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
}
function convexHull(points: Point[]): Point[] {
  const sorted = points.slice().sort((a, b) => a.x - b.x || a.y - b.y),
    unique: Point[] = [];
  for (const p of sorted) if (!unique.length || !same(p, unique[unique.length - 1]!)) unique.push(p);
  if (unique.length < 3) return unique;
  const half = (ps: Iterable<Point>): Point[] => {
    const h: Point[] = [];
    for (const p of ps) {
      while (h.length > 1 && cross(h[h.length - 2]!, h[h.length - 1]!, p) <= 0) h.pop();
      h.push(p);
    }
    return h;
  };
  const a = half(unique),
    b = half(unique.toReversed());
  a.pop();
  b.pop();
  return a.concat(b);
}
const angle = (x: number, y: number): number => {
  const a = ((((Math.atan2(y, x) * 180) / Math.PI) % 180) + 180) % 180;
  return Object.is(a, -0) ? 0 : a;
};
function measureHull(h: Point[]): Measurements {
  const origin = h[0]!;
  if (h.length === 1)
    return {
      min_width_mm: 0,
      min_width_angle_deg: 0,
      rectangle: {
        width_mm: 0,
        height_mm: 0,
        angle_deg: 0,
        area_mm2: 0,
        corners: [origin, origin, origin, origin].map((p) => ({ ...p })),
      },
    };
  const local = h.map((p) => ({ x: p.x - origin.x, y: p.y - origin.y }));
  if (h.length === 2) {
    const b = h[1]!,
      p = local[1]!,
      a = angle(p.x, p.y),
      corners = p.y < 0 ? [b, origin, origin, b] : [origin, b, b, origin];
    return {
      min_width_mm: 0,
      min_width_angle_deg: a,
      rectangle: {
        width_mm: Math.hypot(p.x, p.y),
        height_mm: 0,
        angle_deg: a,
        area_mm2: 0,
        corners: corners.map((q) => ({ ...q })),
      },
    };
  }
  const n = h.length,
    first = local[1]!,
    length = Math.hypot(first.x, first.y),
    u0 = { x: first.x / length, y: first.y / length };
  const dot = (index: number, x: number, y: number): number => local[index]!.x * x + local[index]!.y * y;
  const directions = [
    [u0.x, u0.y],
    [-u0.x, -u0.y],
    [-u0.y, u0.x],
    [u0.y, -u0.x],
  ];
  const supports = directions.map(([x, y]) => {
    let index = 0;
    for (let j = 1; j < n; j++) if (dot(j, x!, y!) > dot(index, x!, y!)) index = j;
    return index;
  });
  const support = (which: number, x: number, y: number): number => {
    let i = supports[which]!;
    // Each of the four indices makes at most one circuit as edge angles turn.
    while (dot((i + 1) % n, x, y) > dot(i, x, y)) i = (i + 1) % n;
    supports[which] = i;
    return dot(i, x, y);
  };
  let minWidth = Infinity,
    minAngle = 0,
    best: Measurements['rectangle'] | undefined;
  for (let i = 0; i < n; i++) {
    const a = local[i]!,
      b = local[(i + 1) % n]!,
      dx = b.x - a.x,
      dy = b.y - a.y,
      L = Math.hypot(dx, dy),
      ux = dx / L,
      uy = dy / L,
      vx = -uy,
      vy = ux;
    const maxU = support(0, ux, uy),
      minU = -support(1, -ux, -uy),
      maxV = support(2, vx, vy),
      minV = -support(3, -vx, -vy);
    const w = Math.max(0, maxU - minU),
      height = Math.max(0, maxV - minV),
      area = w * height,
      edgeAngle = angle(ux, uy);
    if (height < minWidth || (height === minWidth && edgeAngle < minAngle)) {
      minWidth = height;
      minAngle = edgeAngle;
    }
    const rectangleAngle = w >= height ? edgeAngle : angle(vx, vy);
    const areaTie = best ? Number.EPSILON * 64 * Math.max(1, area, best.area_mm2) : 0;
    if (
      !best ||
      area < best.area_mm2 - areaTie ||
      (Math.abs(area - best.area_mm2) <= areaTie && rectangleAngle < best.angle_deg)
    ) {
      const make = (u: number, v: number): Point => ({
        x: origin.x + ux * u + vx * v,
        y: origin.y + uy * u + vy * v,
      });
      let corners = [make(minU, minV), make(maxU, minV), make(maxU, maxV), make(minU, maxV)];
      if (height > w) corners = [corners[1]!, corners[2]!, corners[3]!, corners[0]!];
      const direction = { x: corners[1]!.x - corners[0]!.x, y: corners[1]!.y - corners[0]!.y };
      if (Math.atan2(direction.y, direction.x) < 0)
        corners = [corners[2]!, corners[3]!, corners[0]!, corners[1]!];
      best = {
        width_mm: Math.max(w, height),
        height_mm: Math.min(w, height),
        angle_deg: rectangleAngle,
        area_mm2: area,
        corners,
      };
    }
  }
  return { min_width_mm: minWidth, min_width_angle_deg: minAngle, rectangle: best! };
}

/**
 * Pure geometry, millimetres. Hausdorff error bounds minimum width and projected
 * dimensions at the reported rectangle angle by 2*tolerance. They do not bound
 * orientation, area, or dimensions at an alternative exact optimum orientation.
 */
export function measureSnapshot(snapshot: unknown, toleranceMm: number) {
  const checked = validate(snapshot, toleranceMm),
    all: Point[] = [],
    objects: Array<Measurements & { static_id: number }> = [];
  let count = 0;
  for (const object of checked.snapshot.objects) {
    const points: Point[] = [];
    const emit = (p: Point): void => {
      if (points.length && same(points[points.length - 1]!, p)) return;
      // A repeated start point closes the path and need not occupy another slot.
      if (points.length > 1 && same(points[0]!, p)) return;
      if (++count > LIMIT)
        fail('GEOMETRY_LIMIT_EXCEEDED', 'Превышен общий лимит 200000 точек; допуск не изменён');
      points.push(p);
    };
    for (const segment of object.segments) {
      emit(segment.p0);
      if (segment.kind === 'line') emit(segment.p1);
      else flatten(segment, toleranceMm / 2, emit);
    }
    const hull = convexHull(points);
    objects.push({ static_id: object.static_id, ...measureHull(hull) });
    for (const p of hull) all.push(p);
  }
  // The hull of a union equals the hull of the individual hulls.
  return {
    objects,
    aggregate: measureHull(convexHull(all)),
    units: 'mm' as const,
    tolerance_mm: toleranceMm,
    boundary_error_mm: toleranceMm,
    dimension_error_mm: 2 * toleranceMm,
  };
}
