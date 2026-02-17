namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// Minimal WGSL shaders for WebGPU canvas output.
/// Only contains the final blit pipeline — all image processing is done by
/// ILGPU C# compute kernels in RenderKernels.cs.
///
/// The blit pipeline takes an RGBA texture (produced by ILGPU) and renders
/// it to the canvas swap chain via a render pass. This is required because
/// WebGPU compute shaders cannot write directly to the canvas texture.
/// </summary>
public static class WGPUShaders
{
    /// <summary>
    /// Combined vertex + fragment module for blitting an RGBA texture to the canvas.
    /// Full-screen triangle (no vertex buffer needed — vertices generated in shader).
    /// </summary>
    public const string BlitToCanvas = @"
struct VsOut {
    @builtin(position) pos : vec4f,
    @location(0) uv : vec2f,
};

// Full-screen triangle — generates 3 vertices that cover the entire viewport
// No vertex buffer needed. Invoke with draw(3).
@vertex
fn vs_main(@builtin(vertex_index) vi : u32) -> VsOut {
    var out : VsOut;
    let x = f32(i32(vi & 1u)) * 4.0 - 1.0;
    let y = f32(i32(vi >> 1u)) * 4.0 - 1.0;
    out.pos = vec4f(x, y, 0.0, 1.0);
    out.uv = vec2f((x + 1.0) * 0.5, 1.0 - (y + 1.0) * 0.5);
    return out;
}

@group(0) @binding(0) var u_sampler : sampler;
@group(0) @binding(1) var u_texture : texture_2d<f32>;

// Uniform: vec4f(texW, texH, canvasW, canvasH)
@group(0) @binding(2) var<uniform> u_dims : vec4f;

@fragment
fn fs_main(@location(0) uv : vec2f) -> @location(0) vec4f {
    let texW = u_dims.x;
    let texH = u_dims.y;
    let canW = u_dims.z;
    let canH = u_dims.w;

    // Compute aspect-fit scale
    let texAspect = texW / texH;
    let canAspect = canW / canH;

    var fitW : f32;
    var fitH : f32;
    if (texAspect > canAspect) {
        fitW = 1.0;
        fitH = canAspect / texAspect;
    } else {
        fitW = texAspect / canAspect;
        fitH = 1.0;
    }

    // Map canvas UV to texture UV
    let offsetX = (1.0 - fitW) * 0.5;
    let offsetY = (1.0 - fitH) * 0.5;
    let texU = (uv.x - offsetX) / fitW;
    let texV = (uv.y - offsetY) / fitH;

    // Always sample (textureSample requires uniform control flow)
    let sampled = textureSample(u_texture, u_sampler, vec2f(clamp(texU, 0.0, 1.0), clamp(texV, 0.0, 1.0)));
    let bg = vec4f(0.008, 0.008, 0.035, 1.0);

    // Check if inside the fitted region
    let inside = uv.x >= offsetX && uv.x <= 1.0 - offsetX &&
                 uv.y >= offsetY && uv.y <= 1.0 - offsetY;

    return select(bg, sampled, inside);
}
";

    /// <summary>
    /// Blit fragment for GPUExternalTexture (video frame).
    /// Used for the fast path: video → canvas with no ILGPU processing.
    /// </summary>
    public const string BlitExternalToCanvas = @"
struct VsOut {
    @builtin(position) pos : vec4f,
    @location(0) uv : vec2f,
};

@vertex
fn vs_main(@builtin(vertex_index) vi : u32) -> VsOut {
    var out : VsOut;
    let x = f32(i32(vi & 1u)) * 4.0 - 1.0;
    let y = f32(i32(vi >> 1u)) * 4.0 - 1.0;
    out.pos = vec4f(x, y, 0.0, 1.0);
    out.uv = vec2f((x + 1.0) * 0.5, 1.0 - (y + 1.0) * 0.5);
    return out;
}

@group(0) @binding(0) var u_sampler : sampler;
@group(0) @binding(1) var u_texture : texture_external;

@fragment
fn fs_main(@location(0) uv : vec2f) -> @location(0) vec4f {
    return textureSampleBaseClampToEdge(u_texture, u_sampler, uv);
}
";

    /// <summary>
    /// Render video frame to an RGBA render target texture (not the canvas).
    /// Used when ILGPU processing is needed: video → texture → readback → ILGPU.
    /// Same as BlitExternalToCanvas but rendered to an FBO texture.
    /// </summary>
    public const string VideoToTexture = @"
struct VsOut {
    @builtin(position) pos : vec4f,
    @location(0) uv : vec2f,
};

@vertex
fn vs_main(@builtin(vertex_index) vi : u32) -> VsOut {
    var out : VsOut;
    let x = f32(i32(vi & 1u)) * 4.0 - 1.0;
    let y = f32(i32(vi >> 1u)) * 4.0 - 1.0;
    out.pos = vec4f(x, y, 0.0, 1.0);
    out.uv = vec2f((x + 1.0) * 0.5, 1.0 - (y + 1.0) * 0.5);
    return out;
}

@group(0) @binding(0) var u_sampler : sampler;
@group(0) @binding(1) var u_texture : texture_external;

@fragment
fn fs_main(@location(0) uv : vec2f) -> @location(0) vec4f {
    return textureSampleBaseClampToEdge(u_texture, u_sampler, uv);
}
";

    /// <summary>
    /// Compute shader that copies packed RGBA uint data from a storage buffer
    /// to a storage texture. This avoids WebGPU's bytesPerRow 256-byte alignment
    /// requirement that copyBufferToTexture enforces.
    /// </summary>
    public const string BufferToTexture = @"
@group(0) @binding(0) var<storage, read> src : array<u32>;
@group(0) @binding(1) var dst : texture_storage_2d<rgba8unorm, write>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid : vec3<u32>) {
    let dims = textureDimensions(dst);
    if (gid.x >= dims.x || gid.y >= dims.y) { return; }
    let idx = gid.y * dims.x + gid.x;
    let packed = src[idx];
    let r = f32(packed & 0xFFu) / 255.0;
    let g = f32((packed >> 8u) & 0xFFu) / 255.0;
    let b = f32((packed >> 16u) & 0xFFu) / 255.0;
    let a = f32((packed >> 24u) & 0xFFu) / 255.0;
    textureStore(dst, vec2<u32>(gid.x, gid.y), vec4<f32>(r, g, b, a));
}
";

    /// <summary>
    /// Compute shader that reads an RGBA texture and writes packed uint values
    /// to a storage buffer. Inverse of BufferToTexture.
    /// Used to capture video frames/images into ILGPU buffers for processing.
    /// </summary>
    public const string TextureToBuffer = @"
@group(0) @binding(0) var src : texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> dst : array<u32>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid : vec3<u32>) {
    let dims = textureDimensions(src);
    if (gid.x >= dims.x || gid.y >= dims.y) { return; }
    let c = textureLoad(src, vec2<i32>(vec2<u32>(gid.x, gid.y)), 0);
    let r = u32(c.r * 255.0);
    let g = u32(c.g * 255.0);
    let b = u32(c.b * 255.0);
    let a = u32(c.a * 255.0);
    dst[gid.y * dims.x + gid.x] = r | (g << 8u) | (b << 16u) | (a << 24u);
}
";

    /// <summary>
    /// Full-screen quad that samples a 2D RGBA texture and outputs with alpha.
    /// Pipeline is configured with alpha blending so this composites over existing content.
    /// Used to overlay Canvas 2D UI onto the swap chain after the video blit.
    /// </summary>
    public const string UIOverlayBlit = @"
struct VsOut {
    @builtin(position) pos : vec4f,
    @location(0) uv : vec2f,
};

@vertex
fn vs_main(@builtin(vertex_index) vi : u32) -> VsOut {
    var out : VsOut;
    let x = f32(i32(vi & 1u)) * 4.0 - 1.0;
    let y = f32(i32(vi >> 1u)) * 4.0 - 1.0;
    out.pos = vec4f(x, y, 0.0, 1.0);
    out.uv = vec2f((x + 1.0) * 0.5, 1.0 - (y + 1.0) * 0.5);
    return out;
}

@group(0) @binding(0) var u_sampler : sampler;
@group(0) @binding(1) var u_texture : texture_2d<f32>;

@fragment
fn fs_main(@location(0) uv : vec2f) -> @location(0) vec4f {
    return textureSample(u_texture, u_sampler, uv);
}
";
}
