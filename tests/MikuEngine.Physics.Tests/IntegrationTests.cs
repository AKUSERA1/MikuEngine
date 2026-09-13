using MikuEngine.Physics;
using Xunit;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// B6 集成与 S1 单测（方案 §4-B6 / §5）：全部走引擎主入口
/// <c>Update(enabled, bw, bib, frame, prevFrame)</c>。
/// S1-GATE-1/2（gate 语义：OFF=动画 / ON=物理驱动骨写回）、
/// S1-TOG-1/2（开关边沿：OFF→ON 强制 Reset 无跳变 / ON→OFF 回动画）、
/// DET-1（同配置双跑逐位一致）、DET-2（时钟模式解耦：144Hz RealTime 游标
/// vs FrameLocked 在同动画帧号处物理状态逐位相等）。
/// </summary>
public class IntegrationTests
{
    private const double AnimXPerFrame = 0.01;

    private static void ApplyMovingAnimation(float[] bw, double frame)
    {
        float x = (float)(frame * AnimXPerFrame);
        SyncLayerTests.SetBoneTranslation(bw, 0, x, 0, 0);
        SyncLayerTests.SetBoneTranslation(bw, 1, x, 0, 0);
    }

    private static void ApplyStillAnimation(float[] bw)
    {
        SyncLayerTests.SetBoneTranslation(bw, 0, 0, 0, 0);
        SyncLayerTests.SetBoneTranslation(bw, 1, 0, 0, 0);
    }

    private static long HashFloats(float[] a, float[]? b = null)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            for (int i = 0; i < a.Length; i++)
                h = (h ^ (int)MathF.Round(a[i] * 4096f)) * 1099511628211L;
            if (b != null)
            {
                for (int i = 0; i < b.Length; i++)
                    h = (h ^ (int)MathF.Round(b[i] * 4096f)) * 1099511628211L;
            }
            return h;
        }
    }

    // =========================================================================
    // S1-GATE-1：OFF = 动画。物理 OFF 跑 60 帧 → 物理组骨骼世界矩阵保持
    // FK/IK/付与结果（Update 不碰骨骼矩阵），物理段整体未执行。
    // =========================================================================

    [Fact]
    public void S1Gate1_OffKeepsAnimatedBonesAndSkipsPhysics()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreateFreeFallRig();
        for (int n = 0; n <= 59; n++)
        {
            SyncLayerTests.SetBoneTranslation(bw, 0, (float)(n * AnimXPerFrame), 0, 0);
            ph.Update(false, bw, bib, n, n - 1);
            Assert.Equal(0, ph.TeleportCount);
        }
        // bone0 是 B（自由坠落体）的驱动骨：若物理段误执行，坠落会把 y 写成大负值。
        Assert.True(MathF.Abs(bw[0 * 16 + 12] - 59f * (float)AnimXPerFrame) < 1e-5f,
            $"bone0 x {bw[0]} (expect animated)");
        Assert.Equal(0f, bw[0 * 16 + 13]);
        Assert.Equal(0f, bw[0 * 16 + 14]);
    }

    // =========================================================================
    // S1-GATE-2：ON = 物理驱动骨被写回。与 GATE-1 对称的正面：30 帧后自由体
    // 坠落，其驱动骨的 y 分量被物理改写为显著负值（动画 y=0）。
    // =========================================================================

    [Fact]
    public void S1Gate2_OnWritesBackPhysicsDrivenBone()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreateFreeFallRig();
        for (int n = 0; n <= 29; n++)
        {
            SyncLayerTests.SetBoneTranslation(bw, 0, (float)(n * AnimXPerFrame), 0, 0);
            ph.Update(true, bw, bib, n, n - 1);
        }
        // B 坠落 0.5s 至地面：body y ≈ 0.3，bone y = body.y − 5 ≈ −4.7。
        Assert.True(bw[0 * 16 + 13] < -0.01f, $"bone0 y {bw[0 * 16 + 13]} (expect physics-driven drop)");
    }

    // =========================================================================
    // S1-TOG-1：OFF→ON 无爆。先 OFF 30 帧（骨骼匀速移动），ON 首帧：
    // 体位置 == 当前骨骼 snap（boneWorld×offset 精确相等），写回无跳变
    // （alpha=0 显示 snap 姿态 == 动画矩阵）。
    // =========================================================================

    [Fact]
    public void S1Tog1_OffToOnSnapsWithoutJump()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreateFreeFallRig();
        RigidBodyStore store = ph.Store;
        for (int n = 0; n <= 29; n++)
        {
            SyncLayerTests.SetBoneTranslation(bw, 0, (float)(n * AnimXPerFrame), 0, 0);
            ph.Update(false, bw, bib, n, n - 1);
        }

        // ON 首帧（frame=30）：Reset snap + tick 基准同步 → advance=0 → 只 snap。
        SyncLayerTests.SetBoneTranslation(bw, 0, 30f * (float)AnimXPerFrame, 0, 0);
        ph.Update(true, bw, bib, 30, 29);

        float px = store.Positions[0], py = store.Positions[1], pz = store.Positions[2];
        Assert.True(MathF.Abs(px - 30f * (float)AnimXPerFrame) < 1e-4f, $"snap x {px}");
        Assert.True(MathF.Abs(py - 5f) < 1e-4f, $"snap y {py}");
        Assert.True(MathF.Abs(pz) < 1e-4f, $"snap z {pz}");
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0f, store.LinearVelocities[i]);
            Assert.Equal(0f, store.AngularVelocities[i]);
        }
        // 写回无跳变：骨骼矩阵仍 == 动画矩阵。
        Assert.True(MathF.Abs(bw[0 * 16 + 12] - 30f * (float)AnimXPerFrame) < 1e-4f,
            $"bone x {bw[0 * 16 + 12]}");
        Assert.Equal(0f, bw[0 * 16 + 13]);
    }

    // =========================================================================
    // S1-TOG-2：ON→OFF 回动画。ON 30 帧（物理已写回骨矩阵），OFF 后每帧
    // 骨骼矩阵 == 纯动画结果（宿主每帧重设动画、物理不再触碰）。
    // =========================================================================

    [Fact]
    public void S1Tog2_OnToOffReturnsToAnimatedPose()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreateFreeFallRig();
        for (int n = 0; n <= 29; n++)
        {
            SyncLayerTests.SetBoneTranslation(bw, 0, (float)(n * AnimXPerFrame), 0, 0);
            ph.Update(true, bw, bib, n, n - 1);
        }
        Assert.True(bw[0 * 16 + 13] < -0.01f, "precondition: physics wrote back during ON");

        for (int n = 30; n <= 39; n++)
        {
            SyncLayerTests.SetBoneTranslation(bw, 0, (float)(n * AnimXPerFrame), 0, 0);
            ph.Update(false, bw, bib, n, n - 1);
            Assert.True(MathF.Abs(bw[0 * 16 + 12] - (float)(n * AnimXPerFrame)) < 1e-5f,
                $"frame {n}: bone x {bw[0 * 16 + 12]} (expect animated)");
            Assert.Equal(0f, bw[0 * 16 + 13]);
        }
    }

    // =========================================================================
    // DET-1：同配置双跑 300 帧（FrameLocked，匀速骨骼运动 + 物理写回），
    // 骨骼矩阵哈希序列逐帧严格相等（tick 时钟下物理状态是帧号的纯函数）。
    // =========================================================================

    [Fact]
    public void Det1_DuplicateRunsMatchFrameByFrame()
    {
        int frames = 300;
        long[] hashA = new long[frames];
        long[] hashB = new long[frames];

        for (int run = 0; run < 2; run++)
        {
            (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreatePendulumRig();
            long[] target = run == 0 ? hashA : hashB;
            for (int n = 0; n < frames; n++)
            {
                ApplyMovingAnimation(bw, n);
                ph.Update(true, bw, bib, n, n - 1);
                target[n] = HashFloats(bw);
            }
        }

        for (int n = 0; n < frames; n++)
            Assert.True(hashA[n] == hashB[n], $"frame {n}: hash {hashA[n]} vs {hashB[n]}");
    }

    // =========================================================================
    // DET-2：时钟模式解耦。跑 A = FrameLocked（60fps 渲染，每帧 +1 动画帧）；
    // 跑 B = RealTime 游标模拟 144Hz（每渲染帧 +30/144 动画帧）。骨骼静止、
    // B 带初速——target 恒定使两跑输入逐位一致，store 状态只由 tick 数决定：
    // 在两跑共同的整数动画帧号处（tickTarget == 2N）物理状态哈希必须严格相等。
    // 哈希取 store 位姿+速度（tick 末状态），与渲染插值 alpha 无关。
    // =========================================================================

    [Fact]
    public void Det2_RealTime144HzMatchesFrameLockedAtSameFrameNumbers()
    {
        const int frames = 60;
        long?[] hashA = new long?[frames + 1];
        long?[] hashB = new long?[frames + 1];

        // 跑 A：FrameLocked。
        {
            (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreatePendulumRig();
            RigidBodyStore store = ph.Store;
            for (int n = 0; n <= frames; n++)
            {
                ApplyStillAnimation(bw);
                ph.Update(true, bw, bib, n, n - 1);
                if (n == 0)
                {
                    store.LinearVelocities[3] = 2f;
                    store.LinearVelocities[4] = -1f;
                }
                hashA[n] = HashFloats(store.Positions, store.Orientations);
            }
        }

        // 跑 B：144Hz RealTime 游标（帧号增量 30/144 = 5/24）。
        {
            (MMDPhysics ph, float[] bw, float[] bib) = SyncLayerTests.CreatePendulumRig();
            RigidBodyStore store = ph.Store;
            double step = 5.0 / 24.0;
            double frame = 0, prev = -step;
            for (int i = 0; i <= 292; i++)
            {
                ApplyStillAnimation(bw);
                ph.Update(true, bw, bib, frame, prev);
                int tickTarget = (int)System.Math.Floor(frame * MMDPhysics.TickRateMultiplier + 1e-4);
                if (i == 0)
                {
                    store.LinearVelocities[3] = 2f;
                    store.LinearVelocities[4] = -1f;
                }
                // 只在与跑 A 对齐的帧号（tickTarget == 2N）处记录——此时 store
                // 恰好完成第 tickTarget 个 tick，与跑 A 同 tick 数同状态。
                if ((tickTarget & 1) == 0)
                {
                    int n = tickTarget >> 1;
                    if (n <= frames && hashB[n] == null)
                        hashB[n] = HashFloats(store.Positions, store.Orientations);
                }
                prev = frame;
                frame += step;
            }
        }

        for (int n = 0; n <= frames; n++)
        {
            Assert.True(hashB[n] != null, $"144Hz run never landed exactly on frame {n}");
            Assert.True(hashA[n] == hashB[n], $"frame {n}: hash {hashA[n]} vs {hashB[n]}");
        }
    }
}
