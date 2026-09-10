#version 310 es
precision mediump float;

// MikuEngine GLES 3.1 —— 迷雾格网地面 片元着色器
// 由 MikuPlay.Rendering.Vulkan 的 grid.frag 改写而来：
//   #version 450 → #version 310 es
//   layout(set=0, binding=0) → layout(binding=0)
//   新增 precision mediump float;（GLES fragment 必须显式声明）

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
    float lineHit  = max(hitVert, hitHoriz);

    vec2 idx = round(gp);
    float majorEvery = max(u.uGridParams.z, 1.0);
    vec2 modv = abs(mod(idx, majorEvery));

    vec3 lineColor;
    if (hitHoriz > 0.5 && abs(idx.y) < 0.01)      lineColor = u.uAxisXColor.rgb;
    else if (hitVert > 0.5 && abs(idx.x) < 0.01)  lineColor = u.uAxisZColor.rgb;
    else {
        bool major = (hitVert > 0.5 && modv.x < 0.01) || (hitHoriz > 0.5 && modv.y < 0.01);
        lineColor = major ? u.uMajorColor.rgb : u.uMinorColor.rgb;
    }

    vec3 fogColor = u.uFogColor.rgb;
    vec3 gridCol = mix(lineColor, fogColor, fog * 0.9);
    float gridA = lineHit * u.uGridParams.w * edgeFade * (1.0 - fog * 0.72);

    vec3 surfCol = mix(u.uSurface.rgb, fogColor, fog);
    float surfA = u.uSurface.w * edgeFade * (1.0 - fog * 0.85);

    vec3 col = mix(surfCol, gridCol, gridA);
    float alpha = max(surfA, gridA);
    outColor = vec4(col, alpha);
}
