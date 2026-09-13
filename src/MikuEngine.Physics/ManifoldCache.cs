// Persistent contact manifolds — the cache that makes warm starting possible.
// （对照 reze physics/manifold.ts 逐行移植）
//
// Bullet 2.75 keeps a btPersistentManifold per body pair holding up to 4 points,
// each carrying the impulse it converged to last step. At setup the solver seeds
// each row with `cp.m_appliedImpulse * m_warmstartingFactor` (0.85) and applies
// it immediately, so a resting stack starts the substep already holding roughly
// the load it needs instead of rediscovering it from zero every time.
//
// That is not a nicety here: penetration recovery rides in the contact velocity
// row as a Baumgarte term, and a row rebuilt from zero each substep overshoots
// it. Warm starting on its own was measured WORSE on this engine — but that was
// against a solver with no bias term and a position-correction pass, which is a
// different system. The two are one design in Bullet and are ported as one.
//
// Points are matched by proximity in each body's own local frame, which is what
// btPersistentManifold does — a world-space match would drift with the body.

namespace MikuEngine.Physics;

/// <summary>One cached manifold point（对照 reze manifold.ts Point）。</summary>
public sealed class ManifoldPoint
{
    /// <summary>Contact point in each body's local frame (lever arms are world-space and
    /// rotate with the body, so they cannot be compared across substeps).</summary>
    public float Lax;
    public float Lay;
    public float Laz;
    public float Lbx;
    public float Lby;
    public float Lbz;
    public float NormalImpulse;
    public float FrictionImpulse1;
    public float FrictionImpulse2;
    /// <summary>Substeps this point has survived — Bullet disables restitution past
    /// m_restingContactRestitutionThreshold (2) so resting contacts stop bouncing.</summary>
    public int Age;
    /// <summary>Marks points seen this substep; unseen ones are dropped.</summary>
    public bool Seen;
}

public sealed class ManifoldCache
{
    private const int MaxPoints = 4;

    /// <summary>Match radius, in model units. Bullet's gContactBreakingThreshold is 0.02;
    /// ours is the contact margin, so a point that merely slid along a face is
    /// still recognised as the same point rather than dropped and rebuilt.</summary>
    private const float MatchDistSq = 0.04f * 0.04f;

    private readonly Dictionary<int, List<ManifoldPoint>> _pairs = new();
    private readonly HashSet<int> _touched = new();

    private static int Key(int a, int b) => a < b ? a * 65536 + b : b * 65536 + a;

    /// <summary>Look up what this contact point converged to last substep. Returns null
    /// when it is new.</summary>
    public ManifoldPoint? Find(int a, int b, float lax, float lay, float laz)
    {
        if (!_pairs.TryGetValue(Key(a, b), out List<ManifoldPoint>? list)) return null;
        ManifoldPoint? best = null;
        float bestD = MatchDistSq;
        for (int i = 0; i < list.Count; i++)
        {
            ManifoldPoint p = list[i];
            float dx = p.Lax - lax, dy = p.Lay - lay, dz = p.Laz - laz;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>Record what this point converged to, for the next substep to start from.</summary>
    public void Store(
        int a, int b,
        float lax, float lay, float laz,
        float lbx, float lby, float lbz,
        float normalImpulse, float frictionImpulse1, float frictionImpulse2,
        int age)
    {
        int k = Key(a, b);
        _touched.Add(k);
        if (!_pairs.TryGetValue(k, out List<ManifoldPoint>? list))
        {
            list = new List<ManifoldPoint>();
            _pairs.Add(k, list);
        }

        // Replace the nearest existing point, else append; past MAX_POINTS drop the
        // shallowest-held one so the manifold keeps the load-bearing corners.
        int best = -1;
        float bestD = MatchDistSq;
        for (int i = 0; i < list.Count; i++)
        {
            ManifoldPoint p = list[i];
            float dx = p.Lax - lax, dy = p.Lay - lay, dz = p.Laz - laz;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0)
        {
            if (list.Count < MaxPoints)
            {
                list.Add(new ManifoldPoint
                {
                    Lax = lax, Lay = lay, Laz = laz,
                    Lbx = lbx, Lby = lby, Lbz = lbz,
                    NormalImpulse = normalImpulse,
                    FrictionImpulse1 = frictionImpulse1,
                    FrictionImpulse2 = frictionImpulse2,
                    Age = age,
                    Seen = true,
                });
                return;
            }
            // btPersistentManifold::sortCachedPoints — when a 5th point arrives, drop
            // whichever of the 5 leaves the largest quadrilateral. Area is what keeps
            // a resting box from pivoting; dropping the shallowest instead can leave
            // four nearly-collinear points that pin position but not orientation.
            best = WorstAreaIndex(list, lax, lay, laz);
        }
        ManifoldPoint slot = list[best];
        slot.Lax = lax; slot.Lay = lay; slot.Laz = laz;
        slot.Lbx = lbx; slot.Lby = lby; slot.Lbz = lbz;
        slot.NormalImpulse = normalImpulse;
        slot.FrictionImpulse1 = frictionImpulse1;
        slot.FrictionImpulse2 = frictionImpulse2;
        slot.Age = age;
        slot.Seen = true;
    }

    /// <summary>
    /// Drop every point not re-seen this substep, and every pair left empty.
    /// Without this a separated pair keeps handing back a stale impulse.
    /// （Dictionary 枚举期间仅允许删除、不允许新增——本循环只有删除，安全）
    /// </summary>
    public void EndStep()
    {
        foreach (KeyValuePair<int, List<ManifoldPoint>> kv in _pairs)
        {
            List<ManifoldPoint> list = kv.Value;
            if (!_touched.Contains(kv.Key)) { _pairs.Remove(kv.Key); continue; }
            int w = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ManifoldPoint p = list[i];
                if (!p.Seen) continue;
                p.Seen = false;
                list[w++] = p;
            }
            if (w < list.Count) list.RemoveRange(w, list.Count - w);
            if (w == 0) _pairs.Remove(kv.Key);
        }
        _touched.Clear();
    }

    public void Clear()
    {
        _pairs.Clear();
        _touched.Clear();
    }

    /// <summary>Which of the 4 cached points to replace so the surviving quad keeps the most
    /// area once the new point joins it.</summary>
    private static int WorstAreaIndex(List<ManifoldPoint> list, float nx, float ny, float nz)
    {
        // The quad is: the new point plus the three survivors.
        Span<(float X, float Y, float Z)> pts = stackalloc (float, float, float)[4];
        int bestIdx = 0;
        float bestArea = -1;
        for (int drop = 0; drop < list.Count; drop++)
        {
            pts[0] = (nx, ny, nz);
            int w = 1;
            for (int i = 0; i < list.Count; i++)
            {
                if (i == drop) continue;
                ManifoldPoint p = list[i];
                pts[w++] = (p.Lax, p.Lay, p.Laz);
            }
            if (w < 4) continue;
            // |d0 × d1| over the diagonals — Bullet's area proxy.
            float d0x = pts[0].X - pts[2].X, d0y = pts[0].Y - pts[2].Y, d0z = pts[0].Z - pts[2].Z;
            float d1x = pts[1].X - pts[3].X, d1y = pts[1].Y - pts[3].Y, d1z = pts[1].Z - pts[3].Z;
            float cx = d0y * d1z - d0z * d1y;
            float cy = d0z * d1x - d0x * d1z;
            float cz = d0x * d1y - d0y * d1x;
            float area = cx * cx + cy * cy + cz * cz;
            if (area > bestArea) { bestArea = area; bestIdx = drop; }
        }
        return bestIdx;
    }
}
