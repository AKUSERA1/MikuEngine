namespace MikuEngine.Physics;

/// <summary>
/// One narrowphase contact point.（对照 reze physics/contact.ts Contact 逐字段移植）
///
/// Contact convention: <c>Normal</c> points from body A toward body B, so a
/// positive normal impulse pushes B away from A. <c>RA</c>/<c>RB</c> are
/// world-space lever arms from each CG to the contact point. Depth is positive
/// when shapes overlap, ≤ 0 for speculative contacts inside the margin band.
///
/// The applied-impulse and per-substep cache fields (cAxN…jacInvT2) are written
/// by the constraint solver (B4) — they are declared here so the manifold layout
/// matches reze one-to-one and B4 does not need to touch detection code.
/// </summary>
public sealed class Contact
{
    public int BodyA;
    public int BodyB;

    // Lever arms (world-space) from each body's CG to the contact point.
    public float RAx;
    public float RAy;
    public float RAz;
    public float RBx;
    public float RBy;
    public float RBz;

    // Unit normal pointing A → B.
    public float Nx;
    public float Ny;
    public float Nz;

    public float Depth;
    public float Friction;
    public float Restitution;

    // SI-row state, written by the solver each iter.
    public float AppliedNormalImpulse;
    public float AppliedFrictionImpulse1;
    public float AppliedFrictionImpulse2;

    // Per-substep cache. Written by the solver's setup pass, read by iter.
    // Normal row:
    public float CAxN; public float CAyN; public float CAzN;   // rA × n
    public float CBxN; public float CByN; public float CBzN;   // rB × n
    public float JacInvN;
    /// <summary>Restitution reference, captured at setup from initial relVelN.</summary>
    public float BounceVel;
    /// <summary>
    /// Substeps this contact point has persisted (from the manifold cache).
    /// Bullet kills restitution past m_restingContactRestitutionThreshold = 2.
    /// </summary>
    public int Age;
    /// <summary>
    /// Penetration term routed to the SPLIT (pseudo-velocity) channel instead of
    /// the real one, when the contact is deeper than the split threshold.
    /// </summary>
    public float RhsPenetration;
    /// <summary>Accumulated impulse on the split channel — Bullet's m_appliedPushImpulse.</summary>
    public float AppliedPushImpulse;
    /// <summary>
    /// Baumgarte bias, depth·ERP/dt — Bullet 2.75's positionalError. Positive
    /// when penetrating (pushes apart), negative when separated (allows the
    /// approach that closes the gap). See CONTACT_ERP.
    /// </summary>
    public float BiasVel;

    // Friction tangent 1:
    public float T1x; public float T1y; public float T1z;
    public float CAxT1; public float CAyT1; public float CAzT1;
    public float CBxT1; public float CByT1; public float CBzT1;
    public float JacInvT1;

    // Friction tangent 2:
    public float T2x; public float T2y; public float T2z;
    public float CAxT2; public float CAyT2; public float CAzT2;
    public float CBxT2; public float CByT2; public float CBzT2;
    public float JacInvT2;
}

/// <summary>
/// Pool of reusable Contact objects.（对照 reze ContactPool；零分配——acquire 复用
/// 池内已有对象，只有池增长时才 new，而池上界由 pair 数决定、随首帧稳定）
/// </summary>
public sealed class ContactPool
{
    private Contact[] _pool = Array.Empty<Contact>();
    public int Count { get; private set; }

    public Contact Acquire()
    {
        if (Count == _pool.Length)
        {
            int newCap = _pool.Length == 0 ? 16 : _pool.Length * 2;
            Array.Resize(ref _pool, newCap);
        }
        // 扩容后的槽位惰性填充（reze push 语义：每槽只在首次用到时创建一次）
        Contact c = _pool[Count] ??= new Contact();
        c.AppliedNormalImpulse = 0;
        c.AppliedFrictionImpulse1 = 0;
        c.AppliedFrictionImpulse2 = 0;
        c.AppliedPushImpulse = 0;
        Count++;
        return c;
    }

    public void Reset()
    {
        Count = 0;
    }

    public Contact Get(int i)
    {
        return _pool[i];
    }
}
