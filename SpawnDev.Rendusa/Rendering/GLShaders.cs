namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// GLSL shader source strings for the WebGL2 media player pipeline.
/// All shaders are WebGL2 (GLSL ES 3.00).
/// </summary>
public static class GLShaders
{
    /// <summary>
    /// Vertex shader for rendering a textured quad.
    /// The quad is positioned via u_rect (x, y, w, h) in clip space (-1..1).
    /// Vertices are unit-square (0..1); texCoords flip Y for GL convention.
    /// </summary>
    public const string QuadVertex = @"#version 300 es
precision highp float;
in vec2 a_position;
in vec2 a_texCoord;
out vec2 v_texCoord;
uniform vec4 u_rect;   // x, y, w, h in clip space
void main() {
    vec2 pos = u_rect.xy + a_position * u_rect.zw;
    gl_Position = vec4(pos, 0.0, 1.0);
    v_texCoord = a_texCoord;
}";

    /// <summary>
    /// Fragment shader that samples a texture with opacity control.
    /// </summary>
    public const string TextureFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_texture;
uniform float u_opacity;
void main() {
    vec4 c = texture(u_texture, v_texCoord);
    fragColor = vec4(c.rgb, c.a * u_opacity);
}";

    /// <summary>
    /// Fragment shader that outputs a solid color.
    /// Used for control backgrounds, seek bar fill, etc.
    /// </summary>
    public const string SolidFrag = @"#version 300 es
precision highp float;
out vec4 fragColor;
uniform vec4 u_color;
void main() {
    fragColor = u_color;
}";

    /// <summary>
    /// Fragment shader for audio frequency-bar visualization.
    /// Reads FFT data from a 1D luminance texture and draws vertical bars
    /// with a blue-to-cyan gradient and subtle glow at the tops.
    /// </summary>
    public const string AudioVizFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_fftTexture;
uniform float u_time;
void main() {
    float freq = texture(u_fftTexture, vec2(v_texCoord.x, 0.5)).r;
    float bar = step(1.0 - v_texCoord.y, freq);
    // Gradient: deep blue → bright cyan based on frequency position
    vec3 lo = vec3(0.05, 0.05, 0.2);
    vec3 hi = vec3(0.3, 0.7, 1.0);
    vec3 col = mix(lo, hi, v_texCoord.x) * bar;
    // Glow at bar tops
    float glow = smoothstep(0.0, 0.05, freq - (1.0 - v_texCoord.y)) * 0.5;
    col += vec3(0.4, 0.6, 1.0) * glow;
    fragColor = vec4(col, 1.0);
}";

    /// <summary>
    /// Fragment shader for vertical gradient overlays.
    /// Draws a gradient from u_colorTop (transparent) to u_colorBottom (semi-opaque).
    /// Used for control bar and title overlay backgrounds.
    /// </summary>
    public const string GradientFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform vec4 u_colorTop;
uniform vec4 u_colorBottom;
void main() {
    fragColor = mix(u_colorBottom, u_colorTop, v_texCoord.y);
}";

    /// <summary>
    /// Fragment shader for rounded rectangles.
    /// Computes SDF-based rounded corners with anti-aliasing.
    /// u_rectSize = (width, height) in pixels; u_radius = corner radius in pixels.
    /// </summary>
    public const string RoundedRectFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform vec4 u_color;
uniform vec2 u_rectSize;   // width, height in pixels
uniform float u_radius;    // corner radius in pixels
void main() {
    vec2 p = v_texCoord * u_rectSize;
    vec2 q = abs(p - u_rectSize * 0.5) - (u_rectSize * 0.5 - u_radius);
    float d = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - u_radius;
    float aa = fwidth(d);
    float alpha = 1.0 - smoothstep(-aa, aa, d);
    fragColor = vec4(u_color.rgb, u_color.a * alpha);
}";

    // ── Stereo 3D Shaders ────────────────────────────────────────

    /// <summary>
    /// Fragment shader that extracts one eye from a packed stereo source.
    /// u_inputFormat: 0 = full (mono), 1 = SBS-left, 2 = SBS-right,
    ///                3 = OU-top (left), 4 = OU-bottom (right),
    ///                5 = mosaic tile (uses u_mosaicGrid and u_tileIndex).
    /// Remaps UVs to sample the correct region of the source texture.
    /// </summary>
    public const string StereoExtractFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_texture;
uniform int u_inputFormat;    // 0=mono, 1=SBS-L, 2=SBS-R, 3=OU-T, 4=OU-B, 5=mosaic
uniform vec2 u_mosaicGrid;    // (cols, rows) — only used when inputFormat==5
uniform vec2 u_tileIndex;     // (col, row) — which tile to extract (0-based)
void main() {
    vec2 uv = vec2(v_texCoord.x, 1.0 - v_texCoord.y); // un-flip Y for FBO rendering
    if (u_inputFormat == 1) {        // SBS left eye: left half
        uv.x = uv.x * 0.5;
    } else if (u_inputFormat == 2) { // SBS right eye: right half
        uv.x = uv.x * 0.5 + 0.5;
    } else if (u_inputFormat == 3) { // OU top (left eye): top half
        uv.y = uv.y * 0.5 + 0.5;
    } else if (u_inputFormat == 4) { // OU bottom (right eye): bottom half
        uv.y = uv.y * 0.5;
    } else if (u_inputFormat == 5) { // Mosaic: extract tile at (col,row)
        float tileW = 1.0 / u_mosaicGrid.x;
        float tileH = 1.0 / u_mosaicGrid.y;
        uv.x = uv.x * tileW + u_tileIndex.x * tileW;
        uv.y = uv.y * tileH + (u_mosaicGrid.y - 1.0 - u_tileIndex.y) * tileH; // row 0 = top
    }
    // inputFormat == 0: full texture, no remapping
    fragColor = texture(u_texture, uv);
}";

    /// <summary>
    /// Fragment shader that composites left and right eye textures into
    /// an anaglyph image using configurable 3×3 color-channel mixing matrices.
    /// Red-Cyan: leftMatrix = mat3(1,0,0, 0,0,0, 0,0,0)  → red from left
    ///           rightMatrix = mat3(0,0,0, 0,1,0, 0,0,1) → green+blue from right
    /// u_convergence shifts the right eye UV.x for zero-parallax control.
    /// </summary>
    public const string AnaglyphFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_leftEye;
uniform sampler2D u_rightEye;
uniform mat3 u_leftMatrix;   // color mixing for left eye
uniform mat3 u_rightMatrix;  // color mixing for right eye
uniform float u_convergence; // horizontal UV shift for right eye
void main() {
    vec2 uvL = v_texCoord;
    vec2 uvR = v_texCoord;
    uvR.x += u_convergence;
    vec3 left  = texture(u_leftEye, uvL).rgb;
    vec3 right = texture(u_rightEye, uvR).rgb;
    vec3 mixed = u_leftMatrix * left + u_rightMatrix * right;
    fragColor = vec4(mixed, 1.0);
}";

    /// <summary>
    /// Fragment shader that synthesizes a displaced view from a mono image + depth map.
    /// Uses a forward-search algorithm with per-pixel parallax projection, occlusion
    /// handling (nearest-to-viewer wins), and disocclusion infill (closest-landing
    /// background pixel fills gaps). Adapted from SpawnDev.BlazorJS.MultiView.
    /// 
    /// u_eyeOffset: +left, -right (typically ±0.02)
    /// u_intensity: depth strength / separation (0.0–1.0)
    /// u_convergence: zero-parallax plane (0.0–1.0, default 0.5)
    /// u_resolution: source resolution in pixels (for pixel-step sizing)
    /// </summary>
    public const string DepthDisplaceFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_source;       // color image
uniform sampler2D u_depth;        // single-channel depth map (0=far, 1=near)
uniform float u_eyeOffset;        // +left, -right (typically ±0.01..0.03)
uniform float u_intensity;        // depth strength / separation (0.0–1.0)
uniform float u_convergence;      // zero-parallax depth plane (0.0–1.0)
uniform vec2 u_resolution;        // source resolution in pixels

#define MAX_SEARCH_ITERATIONS 100

void main() {
    vec2 view_uv = vec2(v_texCoord.x, 1.0 - v_texCoord.y); // un-flip Y for FBO rendering

    // View offset = eyeOffset scaled to a view index delta
    // eyeOffset is ±0.02; we treat the magnitude as the view separation
    float viewOffset = u_eyeOffset;

    // Fast path: if eyeOffset ≈ 0, return source pixel directly
    if (abs(viewOffset) < 0.0001) {
        fragColor = texture(u_source, view_uv);
        return;
    }

    // Search direction: if viewOffset > 0 (left eye), near objects shift right,
    // so we search to the RIGHT to find the source pixel.
    float searchDir = sign(viewOffset);

    // Maximum parallax range in UV space for this view
    float maxViewSeparation = abs(viewOffset) * u_intensity;

    // Pixel sizing for 1-pixel steps
    float pixelSizeX = 1.0 / u_resolution.x;

    // Best hit tracking
    float bestDepth = -1.0;     // highest depth (nearest to viewer) wins
    float bestHitDist = 2.0;
    vec2 bestUV = view_uv;

    // Infill tracking (closest miss for disocclusion holes)
    float closestMissDist = 1000.0;
    vec2 closestMissUV = view_uv;
    float closestMissDepth = 1000.0;

    // Forward-search loop: iterate candidate source pixels
    for (int i = 0; i < MAX_SEARCH_ITERATIONS; i++) {
        float offset = float(i) * pixelSizeX;

        // Stop if we've searched beyond the max separation
        if (offset > maxViewSeparation) break;

        // Candidate UV: step outward from current pixel
        vec2 candidateUV = vec2(view_uv.x + (offset * searchDir), view_uv.y);

        // Bounds check
        if (candidateUV.x < 0.0 || candidateUV.x > 1.0) continue;

        // Sample depth at candidate
        float d = texture(u_depth, candidateUV).r;

        // Project: where does this candidate pixel LAND in the output view?
        // parallax = (depth - convergence) × intensity × viewOffset
        float parallax = (d - u_convergence) * u_intensity * viewOffset;

        // The projected position of this candidate in the output view
        float projectedX = candidateUV.x - parallax;

        // Distance from projected position to our output pixel
        float dist = abs(projectedX - view_uv.x);

        // Hit test: within 0.6 pixels = a hit
        bool isHit = dist < (pixelSizeX * 0.6);

        if (isHit) {
            // Occlusion: nearest to viewer (highest depth) wins
            if (d > bestDepth) {
                bestDepth = d;
                bestHitDist = dist;
                bestUV = candidateUV;
            }
        } else {
            // Infill: track closest-landing miss for disocclusion holes
            if (dist < closestMissDist) {
                closestMissDist = dist;
                closestMissUV = candidateUV;
                closestMissDepth = d;
            } else if (abs(dist - closestMissDist) < (pixelSizeX * 0.1)) {
                // Tie-break: prefer background (low depth) to avoid foreground smearing
                if (d < closestMissDepth) {
                    closestMissUV = candidateUV;
                    closestMissDepth = d;
                }
            }
        }
    }

    // Result: use hit if found, otherwise infill from closest miss
    vec2 finalUV = (bestDepth > -1.0) ? bestUV : closestMissUV;

    // Bounds check to prevent edge streaks
    if (finalUV.x < 0.0 || finalUV.x > 1.0) {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    fragColor = texture(u_source, finalUV);
}";

    /// <summary>
    /// Fragment shader that visualizes a depth map using the Turbo colormap.
    /// Maps single-channel depth values (0.0–1.0) to a perceptually-uniform
    /// color spectrum for rich depth visualization.
    /// Uses polynomial approximation of Google's Turbo colormap.
    /// </summary>
    public const string DepthColormapFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_texture;
uniform float u_opacity;

// Turbo colormap — 32-sample LUT with linear interpolation
// Based on Google Research / Anton Mikhailov's colormap
const vec3 TURBO[32] = vec3[32](
    vec3(0.18995, 0.07176, 0.23217),
    vec3(0.22500, 0.16354, 0.45096),
    vec3(0.25107, 0.25237, 0.63374),
    vec3(0.26816, 0.33825, 0.78412),
    vec3(0.27628, 0.42118, 0.88563),
    vec3(0.27543, 0.50115, 0.93514),
    vec3(0.25862, 0.57958, 0.93421),
    vec3(0.21382, 0.65886, 0.88713),
    vec3(0.15844, 0.73551, 0.80186),
    vec3(0.11167, 0.80569, 0.69001),
    vec3(0.09267, 0.86554, 0.56349),
    vec3(0.12014, 0.91193, 0.43328),
    vec3(0.19659, 0.94448, 0.30412),
    vec3(0.31563, 0.96400, 0.18837),
    vec3(0.46710, 0.97145, 0.09563),
    vec3(0.62203, 0.96762, 0.03739),
    vec3(0.74863, 0.94700, 0.01355),
    vec3(0.83060, 0.91337, 0.00739),
    vec3(0.89070, 0.86820, 0.01040),
    vec3(0.93411, 0.81319, 0.01615),
    vec3(0.96479, 0.74990, 0.01873),
    vec3(0.98359, 0.68049, 0.01569),
    vec3(0.99263, 0.60709, 0.01354),
    vec3(0.99346, 0.53215, 0.01616),
    vec3(0.98680, 0.45816, 0.02348),
    vec3(0.97239, 0.38753, 0.03424),
    vec3(0.94994, 0.32262, 0.04538),
    vec3(0.91907, 0.26557, 0.05765),
    vec3(0.87936, 0.21826, 0.07049),
    vec3(0.83043, 0.18236, 0.08320),
    vec3(0.77214, 0.15901, 0.09421),
    vec3(0.70674, 0.14858, 0.10161)
);

vec3 turbo(float t) {
    t = clamp(t, 0.0, 1.0);
    float idx = t * 31.0;
    int lo = int(floor(idx));
    int hi = min(lo + 1, 31);
    float frac = idx - float(lo);
    return mix(TURBO[lo], TURBO[hi], frac);
}

void main() {
    float depth = texture(u_texture, v_texCoord).r;
    vec3 color = turbo(depth);
    fragColor = vec4(color, u_opacity);
}";

    /// <summary>
    /// Fragment shader that renders a single-channel depth texture as RGB grayscale.
    /// Used by Dimenco/autostereoscopic renderers so the depth map appears as
    /// proper grayscale instead of red-only (R32F textures have G=0, B=0).
    /// </summary>
    public const string DepthGrayscaleFrag = @"#version 300 es
precision highp float;
in vec2 v_texCoord;
out vec4 fragColor;
uniform sampler2D u_texture;
uniform float u_opacity;
void main() {
    float d = texture(u_texture, v_texCoord).r;
    fragColor = vec4(d, d, d, u_opacity);
}";
}

