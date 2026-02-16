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

@fragment
fn fs_main(@location(0) uv : vec2f) -> @location(0) vec4f {
    return textureSample(u_texture, u_sampler, uv);
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
}

