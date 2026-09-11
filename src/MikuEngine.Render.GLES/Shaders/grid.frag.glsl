#version 310 es
// 用 highp：vWorldPos 是 ±500 的世界坐标，mediump 在 GLES 上可能是 fp16，
// 远处格网会出现"不连贯/抖动"。桌面 GL 通常把 mediump 当 fp32，所以桌面看不出问题。
precision highp float;

// MikuEngine GLES 3.1 —— 迷雾格网地面 片元着色器
// 按导数的分级淡出，消除远处摩尔纹/锯齿（修复一直存在的格网锯齿问题）

layout(location = 0) in vec3 vWorldPos;
layout(location = 0) out vec4 outColor;

layout(binding = 0) uniform GridUniform {
    mat4 uViewProj;
    vec4 uFogColor;
    vec4 uFadeParams;
    vec4 uGridParams;
    vec4 uMinorColor;
    vec4 uMajorColor;
    vec4 uAxisXColor;
    vec4 uAxisZColor;
    vec4 uSurface;
} u;

void main() {
    float spacing = max(u.uGridParams.x, 1e-4);
    vec2 gp = vWorldPos.xz / spacing;
    vec2 gd = fwidth(gp);
    float centerDist = length(vWorldPos.xz);

    float edgeFade = 1.0 - smoothstep(u.uFadeParams.x, u.uFadeParams.y, centerDist);
    if (edgeFade <= 0.0) {
        outColor = vec4(u.uFogColor.rgb, 0.0);
        return;
    }

    float fog = smoothstep(u.uFadeParams.z, u.uFadeParams.w, centerDist);

    vec2 frac = abs(fract(gp - 0.5) - 0.5);
    float halfLine = u.uGridParams.y * 0.5;
    float sx = smoothstep(halfLine - gd.x, halfLine + gd.x, frac.x);
    float sz = smoothstep(halfLine - gd.y, halfLine + gd.y, frac.y);
    float hitVert  = 1.0 - sx;
    float hitHoriz = 1.0 - sz;

    vec2 idx = round(gp);
    float majorEvery = max(u.uGridParams.z, 1.0);
    vec2 modv = abs(mod(idx, majorEvery));

    bool isAxisX = hitHoriz > 0.5 && abs(idx.y) < 0.01;
    bool isAxisZ = hitVert  > 0.5 && abs(idx.x) < 0.01;
    bool isMajor = !isAxisX && !isAxisZ &&
                   ((hitVert > 0.5 && modv.x < 0.01) || (hitHoriz > 0.5 && modv.y < 0.01));

    vec3 lineColor = isAxisX ? u.uAxisXColor.rgb
                   : isAxisZ ? u.uAxisZColor.rgb
                   : isMajor ? u.uMajorColor.rgb
                             : u.uMinorColor.rgb;

    // ── 反摩尔纹：一个像素里挤进多条线时，按层级把线淡出 ────────────────
    // gd = 一个像素覆盖多少个 minor 格。gd > 1 时 minor 线必然采样混叠。
    // major 线间距是 minor 的 majorEvery 倍，轴只有两条，因此各自的容忍阈值逐级放宽。
    float gdmax     = max(gd.x, gd.y);
    float minorFade = 1.0 - smoothstep(0.25, 1.00, gdmax);
    float majorFade = 1.0 - smoothstep(0.75, 2.50, gdmax);
    float axisFade  = 1.0 - smoothstep(2.00, 6.00, gdmax);
    float fade      = (isAxisX || isAxisZ) ? axisFade
                    : isMajor              ? majorFade
                                           : minorFade;

    float lineHit = max(hitVert, hitHoriz) * fade;

    vec3 fogColor = u.uFogColor.rgb;
    vec3 gridCol = mix(lineColor, fogColor, fog * 0.9);
    float gridA = lineHit * u.uGridParams.w * edgeFade * (1.0 - fog * 0.72);

    vec3 surfCol = mix(u.uSurface.rgb, fogColor, fog);
    float surfA = u.uSurface.w * edgeFade * (1.0 - fog * 0.85);

    vec3 col = mix(surfCol, gridCol, gridA);
    float alpha = max(surfA, gridA);
    outColor = vec4(col, alpha);
}
