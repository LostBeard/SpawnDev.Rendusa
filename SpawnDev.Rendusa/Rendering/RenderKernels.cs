using ILGPU;
using ILGPU.Runtime;

namespace SpawnDev.Rendusa.Rendering;

// ═══════════════════════════════════════════════════════════════
//  Parameter Structs (for kernels exceeding Action<> 16 type-param limit)
// ═══════════════════════════════════════════════════════════════

/// <summary>3x3 color matrix for anaglyph compositing (one per eye).</summary>
public struct ColorMatrix3x3
{
    public float RR, RG, RB;   // → red channel weights
    public float GR, GG, GB;   // → green channel weights
    public float BR, BG, BB;   // → blue channel weights
}

/// <summary>RGBA color for kernels that need color params as a struct.</summary>
public struct ColorRGBA
{
    public float R, G, B, A;
}

/// <summary>
/// ILGPU C# compute kernels for all image processing in the media player.
/// These replace the GLSL fragment shaders from the WebGL2 renderer.
/// 
/// Each kernel operates on RGBA buffers (packed uint: 0xAABBGGRR) and/or
/// float depth buffers. ILGPU transpiles these to WGSL compute shaders
/// that run on the same GPUDevice as the rest of the pipeline.
///
/// RGBA packing convention: uint = R | (G << 8) | (B << 16) | (A << 24)
/// This matches Uint8ClampedArray/ImageData byte ordering.
/// </summary>
public static class RenderKernels
{
    // ═══════════════════════════════════════════════════════════════
    //  Helper: RGBA Pack/Unpack
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Pack 4 floats (0–1) into a uint RGBA pixel.</summary>
    static uint PackRGBA(float r, float g, float b, float a)
    {
        var ri = (uint)Math.Min(Math.Max(r * 255f, 0f), 255f);
        var gi = (uint)Math.Min(Math.Max(g * 255f, 0f), 255f);
        var bi = (uint)Math.Min(Math.Max(b * 255f, 0f), 255f);
        var ai = (uint)Math.Min(Math.Max(a * 255f, 0f), 255f);
        return ri | (gi << 8) | (bi << 16) | (ai << 24);
    }

    /// <summary>Unpack a uint RGBA pixel to float[4].</summary>
    static float UnpackR(uint c) => (c & 0xFF) / 255f;
    static float UnpackG(uint c) => ((c >> 8) & 0xFF) / 255f;
    static float UnpackB(uint c) => ((c >> 16) & 0xFF) / 255f;
    static float UnpackA(uint c) => ((c >> 24) & 0xFF) / 255f;

    // ═══════════════════════════════════════════════════════════════
    //  Depth Displacement (Forward-Search Parallax)
    //  Replaces DepthDisplaceFrag — the most complex kernel
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Forward-search depth displacement for synthesizing stereo views.
    /// For each output pixel, searches along the horizontal axis in the source
    /// to find the pixel whose parallax-projected position lands here.
    ///
    /// Uses nearest-to-viewer occlusion (highest depth wins) and
    /// closest-miss infill for disocclusion holes.
    /// </summary>
    public static void DepthDisplaceKernel(
        Index1D index,
        ArrayView<uint> source,     // RGBA source image
        ArrayView<float> depth,     // normalized depth (0=far, 1=near)
        ArrayView<uint> output,     // RGBA output
        float eyeOffset,            // +left, -right
        float intensity,            // depth strength (0-1)
        float convergence,          // zero-parallax plane (0-1)
        int width,
        int height)
    {
        int x = index % width;
        int y = index / width;

        // Fast path: no displacement
        if (eyeOffset > -0.0001f && eyeOffset < 0.0001f)
        {
            output[index] = source[index];
            return;
        }

        float viewOffset = eyeOffset;
        float searchDir = viewOffset > 0f ? 1f : -1f;
        float maxSep = (viewOffset > 0f ? viewOffset : -viewOffset) * intensity;
        int maxSteps = (int)(maxSep * width);
        if (maxSteps > 100) maxSteps = 100;

        float bestDepth = -1f;
        int bestSrcX = x;

        float closestMissDist = 1000f;
        int closestMissX = x;
        float closestMissDepth = 1000f;

        float pixelSize = 1f / width;

        for (int i = 0; i < maxSteps; i++)
        {
            int candidateX = x + (int)(i * searchDir);
            if (candidateX < 0 || candidateX >= width) continue;

            int srcIdx = y * width + candidateX;
            float d = depth[srcIdx];

            // Where does this candidate land in the output?
            float parallax = (d - convergence) * intensity * viewOffset;
            float projectedX = (candidateX / (float)width) - parallax;
            float outputU = x / (float)width;
            float dist = projectedX - outputU;
            if (dist < 0f) dist = -dist;

            float hitThreshold = pixelSize * 0.6f;

            if (dist < hitThreshold)
            {
                // Hit: nearest-to-viewer wins
                if (d > bestDepth)
                {
                    bestDepth = d;
                    bestSrcX = candidateX;
                }
            }
            else
            {
                // Track closest miss for infill
                if (dist < closestMissDist)
                {
                    closestMissDist = dist;
                    closestMissX = candidateX;
                    closestMissDepth = d;
                }
                else if (dist - closestMissDist < pixelSize * 0.1f && dist - closestMissDist > -(pixelSize * 0.1f))
                {
                    // Tie-break: prefer background (low depth)
                    if (d < closestMissDepth)
                    {
                        closestMissX = candidateX;
                        closestMissDepth = d;
                    }
                }
            }
        }

        int finalX;
        if (bestDepth > -1f)
            finalX = bestSrcX;
        else
            finalX = closestMissX;

        if (finalX < 0 || finalX >= width)
        {
            output[index] = PackRGBA(0f, 0f, 0f, 1f);
        }
        else
        {
            output[index] = source[y * width + finalX];
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stereo Eye Extraction (UV Remapping)
    //  Replaces StereoExtractFrag
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Extract one eye from a packed stereo source.
    /// format: 0=mono (full), 1=SBS-Left, 2=SBS-Right,
    ///         3=OU-Top (Left), 4=OU-Bottom (Right), 5=Mosaic
    /// </summary>
    public static void StereoExtractKernel(
        Index1D index,
        ArrayView<uint> source,     // packed stereo source
        ArrayView<uint> output,     // single-eye output
        int format,
        int srcW, int srcH,         // source dimensions
        int outW, int outH,         // output dimensions
        int mosaicCols, int mosaicRows,
        int tileCol, int tileRow)
    {
        int ox = index % outW;
        int oy = index / outW;

        float u = ox / (float)outW;
        float v = oy / (float)outH;

        // Remap UV based on format
        if (format == 1) // SBS left
        {
            u = u * 0.5f;
        }
        else if (format == 2) // SBS right
        {
            u = u * 0.5f + 0.5f;
        }
        else if (format == 3) // OU top (left eye)
        {
            v = v * 0.5f;
        }
        else if (format == 4) // OU bottom (right eye)
        {
            v = v * 0.5f + 0.5f;
        }
        else if (format == 5) // Mosaic
        {
            float tileW = 1f / mosaicCols;
            float tileH = 1f / mosaicRows;
            u = u * tileW + tileCol * tileW;
            v = v * tileH + tileRow * tileH;
        }

        // Sample nearest (clamp to edge)
        int sx = (int)(u * (srcW - 1));
        if (sx < 0) sx = 0;
        if (sx >= srcW) sx = srcW - 1;
        int sy = (int)(v * (srcH - 1));
        if (sy < 0) sy = 0;
        if (sy >= srcH) sy = srcH - 1;

        output[index] = source[sy * srcW + sx];
    }

    // ═══════════════════════════════════════════════════════════════
    //  Anaglyph Compositing
    //  Replaces AnaglyphFrag
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Red-Cyan anaglyph compositing: left eye → red channel,
    /// right eye → green+blue channels.
    /// leftR/leftG/leftB control the left eye color matrix row weights.
    /// rightR/rightG/rightB control the right eye color matrix row weights.
    /// Standard Red-Cyan: leftR=(1,0,0), rightR=(0,0,0), leftG=(0,0,0), rightG=(0,1,0), etc.
    /// </summary>
    public static void AnaglyphKernel(
        Index1D index,
        ArrayView<uint> leftEye,
        ArrayView<uint> rightEye,
        ArrayView<uint> output,
        ColorMatrix3x3 leftMatrix,    // left eye color matrix
        ColorMatrix3x3 rightMatrix,   // right eye color matrix
        int convergencePixels, int width)
    {
        int x = index % width;
        int y = index / width;

        float lr = UnpackR(leftEye[index]);
        float lg = UnpackG(leftEye[index]);
        float lb = UnpackB(leftEye[index]);

        // Apply convergence shift to right eye
        int rx = x + convergencePixels;
        uint rightPx;
        if (rx >= 0 && rx < width)
            rightPx = rightEye[y * width + rx];
        else
            rightPx = 0;

        float rr = UnpackR(rightPx);
        float rg = UnpackG(rightPx);
        float rb = UnpackB(rightPx);

        float outR = leftMatrix.RR * lr + leftMatrix.RG * lg + leftMatrix.RB * lb + rightMatrix.RR * rr + rightMatrix.RG * rg + rightMatrix.RB * rb;
        float outG = leftMatrix.GR * lr + leftMatrix.GG * lg + leftMatrix.GB * lb + rightMatrix.GR * rr + rightMatrix.GG * rg + rightMatrix.GB * rb;
        float outB = leftMatrix.BR * lr + leftMatrix.BG * lg + leftMatrix.BB * lb + rightMatrix.BR * rr + rightMatrix.BG * rg + rightMatrix.BB * rb;

        output[index] = PackRGBA(outR, outG, outB, 1f);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Depth Colormap (Turbo LUT)
    //  Replaces DepthColormapFrag
    // ═══════════════════════════════════════════════════════════════

    // Turbo colormap LUT — 32 entries stored as packed RGB
    // (Cannot use const arrays in ILGPU kernels, so we use a switch-based LUT)

    /// <summary>
    /// Map depth value (0–1) to a Turbo colormap RGBA pixel.
    /// Uses a 32-sample LUT with linear interpolation.
    /// </summary>
    public static void DepthColormapKernel(
        Index1D index,
        ArrayView<float> depth,
        ArrayView<uint> output)
    {
        float t = depth[index];
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;

        float idx = t * 31f;
        int lo = (int)idx;
        if (lo < 0) lo = 0;
        if (lo > 31) lo = 31;
        int hi = lo + 1;
        if (hi > 31) hi = 31;
        float frac = idx - lo;

        // Get LUT colors
        float loR = 0f, loG = 0f, loB = 0f;
        float hiR = 0f, hiG = 0f, hiB = 0f;
        TurboLUT(lo, ref loR, ref loG, ref loB);
        TurboLUT(hi, ref hiR, ref hiG, ref hiB);

        float r = loR + (hiR - loR) * frac;
        float g = loG + (hiG - loG) * frac;
        float b = loB + (hiB - loB) * frac;

        output[index] = PackRGBA(r, g, b, 1f);
    }

    /// <summary>Turbo colormap lookup by index (0–31).</summary>
    static void TurboLUT(int i, ref float r, ref float g, ref float b)
    {
        // Full Turbo colormap from Google Research
        if (i == 0) { r = 0.18995f; g = 0.07176f; b = 0.23217f; }
        else if (i == 1) { r = 0.22500f; g = 0.16354f; b = 0.45096f; }
        else if (i == 2) { r = 0.25107f; g = 0.25237f; b = 0.63374f; }
        else if (i == 3) { r = 0.26816f; g = 0.33825f; b = 0.78412f; }
        else if (i == 4) { r = 0.27628f; g = 0.42118f; b = 0.88563f; }
        else if (i == 5) { r = 0.27543f; g = 0.50115f; b = 0.93514f; }
        else if (i == 6) { r = 0.25862f; g = 0.57958f; b = 0.93421f; }
        else if (i == 7) { r = 0.21382f; g = 0.65886f; b = 0.88713f; }
        else if (i == 8) { r = 0.15844f; g = 0.73551f; b = 0.80186f; }
        else if (i == 9) { r = 0.11167f; g = 0.80569f; b = 0.69001f; }
        else if (i == 10) { r = 0.09267f; g = 0.86554f; b = 0.56349f; }
        else if (i == 11) { r = 0.12014f; g = 0.91193f; b = 0.43328f; }
        else if (i == 12) { r = 0.19659f; g = 0.94448f; b = 0.30412f; }
        else if (i == 13) { r = 0.31563f; g = 0.96400f; b = 0.18837f; }
        else if (i == 14) { r = 0.46710f; g = 0.97145f; b = 0.09563f; }
        else if (i == 15) { r = 0.62203f; g = 0.96762f; b = 0.03739f; }
        else if (i == 16) { r = 0.74863f; g = 0.94700f; b = 0.01355f; }
        else if (i == 17) { r = 0.83060f; g = 0.91337f; b = 0.00739f; }
        else if (i == 18) { r = 0.89070f; g = 0.86820f; b = 0.01040f; }
        else if (i == 19) { r = 0.93411f; g = 0.81319f; b = 0.01615f; }
        else if (i == 20) { r = 0.96479f; g = 0.74990f; b = 0.01873f; }
        else if (i == 21) { r = 0.98359f; g = 0.68049f; b = 0.01569f; }
        else if (i == 22) { r = 0.99263f; g = 0.60709f; b = 0.01354f; }
        else if (i == 23) { r = 0.99346f; g = 0.53215f; b = 0.01616f; }
        else if (i == 24) { r = 0.98680f; g = 0.45816f; b = 0.02348f; }
        else if (i == 25) { r = 0.97239f; g = 0.38753f; b = 0.03424f; }
        else if (i == 26) { r = 0.94994f; g = 0.32262f; b = 0.04538f; }
        else if (i == 27) { r = 0.91907f; g = 0.26557f; b = 0.05765f; }
        else if (i == 28) { r = 0.87936f; g = 0.21826f; b = 0.07049f; }
        else if (i == 29) { r = 0.83043f; g = 0.18236f; b = 0.08320f; }
        else if (i == 30) { r = 0.77214f; g = 0.15901f; b = 0.09421f; }
        else { r = 0.70674f; g = 0.14858f; b = 0.10161f; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Depth Grayscale
    //  Replaces DepthGrayscaleFrag
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Convert single-channel depth buffer to RGBA grayscale.
    /// </summary>
    public static void DepthGrayscaleKernel(
        Index1D index,
        ArrayView<float> depth,
        ArrayView<uint> output)
    {
        float d = depth[index];
        if (d < 0f) d = 0f;
        if (d > 1f) d = 1f;
        output[index] = PackRGBA(d, d, d, 1f);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Audio Visualization (FFT Bars)
    //  Replaces AudioVizFrag
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// FFT bar visualization: reads frequency values and draws vertical bars
    /// with a blue-to-cyan gradient and glow.
    /// fftData contains 256 frequency bins (normalized 0–1 as floats).
    /// </summary>
    public static void AudioVizKernel(
        Index1D index,
        ArrayView<float> fftData,   // 256 frequency bins (0-1)
        ArrayView<uint> output,
        int width, int height,
        int fftBinCount)
    {
        int x = index % width;
        int y = index / width;

        float u = x / (float)width;
        float v = 1f - (y / (float)height); // flip Y: bottom=0, top=1

        // Which FFT bin?
        int bin = (int)(u * (fftBinCount - 1));
        if (bin < 0) bin = 0;
        if (bin >= fftBinCount) bin = fftBinCount - 1;
        float freq = fftData[bin];

        if (v > freq)
        {
            // Above bar — transparent
            output[index] = PackRGBA(0f, 0f, 0f, 0f);
            return;
        }

        // Gradient: deep blue → bright cyan based on frequency position
        float loR = 0.05f, loG = 0.05f, loB = 0.2f;
        float hiR = 0.3f, hiG = 0.7f, hiB = 1f;
        float r = loR + (hiR - loR) * u;
        float g = loG + (hiG - loG) * u;
        float b = loB + (hiB - loB) * u;

        // Bar glow at top
        float distFromTop = freq - v;
        if (distFromTop < 0.05f)
        {
            float glow = (1f - distFromTop / 0.05f) * 0.5f;
            r = r + 0.4f * glow;
            g = g + 0.6f * glow;
            b = b + 1f * glow;
        }

        output[index] = PackRGBA(r, g, b, 1f);
    }

    // ═══════════════════════════════════════════════════════════════
    //  UI Drawing Kernels
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fill a rectangular region with a solid color (with alpha compositing).
    /// Dispatch size = rectW * rectH (rect-local indexing).
    /// </summary>
    public static void SolidFillKernel(
        Index1D index,
        ArrayView<uint> buffer,
        int bufW, int bufH,
        int rectX, int rectY, int rectW, int rectH,
        float r, float g, float b, float a)
    {
        // Rect-local index → buffer pixel
        int lx = index % rectW;
        int ly = index / rectW;
        int px = rectX + lx;
        int py = rectY + ly;
        if (px < 0 || px >= bufW || py < 0 || py >= bufH) return;

        int bufIdx = py * bufW + px;
        if (a >= 0.999f)
        {
            buffer[bufIdx] = PackRGBA(r, g, b, 1f);
        }
        else
        {
            // Alpha composite: over operator
            uint existing = buffer[bufIdx];
            float er = UnpackR(existing);
            float eg = UnpackG(existing);
            float eb = UnpackB(existing);
            float ea = UnpackA(existing);
            float outA = a + ea * (1f - a);
            float outR = (r * a + er * ea * (1f - a)) / (outA + 0.001f);
            float outG = (g * a + eg * ea * (1f - a)) / (outA + 0.001f);
            float outB = (b * a + eb * ea * (1f - a)) / (outA + 0.001f);
            buffer[bufIdx] = PackRGBA(outR, outG, outB, outA);
        }
    }

    /// <summary>
    /// Fill a rectangular region with a vertical gradient (top color → bottom color).
    /// Dispatch size = rectW * rectH (rect-local indexing).
    /// </summary>
    public static void GradientFillKernel(
        Index1D index,
        ArrayView<uint> buffer,
        int bufW, int bufH,
        int rectX, int rectY, int rectW, int rectH,
        ColorRGBA topColor, ColorRGBA botColor)
    {
        // Rect-local index → buffer pixel
        int lx = index % rectW;
        int ly = index / rectW;
        int px = rectX + lx;
        int py = rectY + ly;
        if (px < 0 || px >= bufW || py < 0 || py >= bufH) return;

        int bufIdx = py * bufW + px;
        float t = ly / (float)(rectH - 1);
        float r = topColor.R + (botColor.R - topColor.R) * t;
        float g = topColor.G + (botColor.G - topColor.G) * t;
        float b = topColor.B + (botColor.B - topColor.B) * t;
        float a = topColor.A + (botColor.A - topColor.A) * t;

        // Alpha composite
        uint existing = buffer[bufIdx];
        float er = UnpackR(existing);
        float eg = UnpackG(existing);
        float eb = UnpackB(existing);
        float ea = UnpackA(existing);
        float outA = a + ea * (1f - a);
        float outR = (r * a + er * ea * (1f - a)) / (outA + 0.001f);
        float outG = (g * a + eg * ea * (1f - a)) / (outA + 0.001f);
        float outB = (b * a + eb * ea * (1f - a)) / (outA + 0.001f);
        buffer[bufIdx] = PackRGBA(outR, outG, outB, outA);
    }

    /// <summary>
    /// SDF-based rounded rectangle fill with anti-aliased edges.
    /// Dispatch size = rectW * rectH (rect-local indexing).
    /// </summary>
    public static void RoundedRectKernel(
        Index1D index,
        ArrayView<uint> buffer,
        int bufW, int bufH,
        int rectX, int rectY, int rectW, int rectH,
        float radius,
        float r, float g, float b, float a)
    {
        // Rect-local index → buffer pixel
        int lx = index % rectW;
        int ly = index / rectW;
        int px = rectX + lx;
        int py = rectY + ly;
        if (px < 0 || px >= bufW || py < 0 || py >= bufH) return;

        int bufIdx = py * bufW + px;

        // SDF for rounded rect (using local coords directly)
        float localX = lx - rectW * 0.5f;
        float localY = ly - rectH * 0.5f;
        float halfW = rectW * 0.5f - radius;
        float halfH = rectH * 0.5f - radius;
        float qx = (localX > 0 ? localX : -localX) - halfW;
        float qy = (localY > 0 ? localY : -localY) - halfH;
        float maxQ = qx > qy ? qx : qy;
        float clampedMaxQ = maxQ < 0f ? maxQ : 0f;
        float posQx = qx > 0f ? qx : 0f;
        float posQy = qy > 0f ? qy : 0f;
        float d = clampedMaxQ + posQx + posQy - radius; // simplified SDF

        // Anti-aliasing: smooth transition over ~1 pixel
        float alpha;
        if (d < -1f) alpha = 1f;
        else if (d > 1f) alpha = 0f;
        else alpha = 0.5f - d * 0.5f;

        float finalA = a * alpha;
        if (finalA < 0.001f) return;

        // Alpha composite
        uint existing = buffer[bufIdx];
        float er = UnpackR(existing);
        float eg = UnpackG(existing);
        float eb = UnpackB(existing);
        float ea = UnpackA(existing);
        float outA = finalA + ea * (1f - finalA);
        float outR = (r * finalA + er * ea * (1f - finalA)) / (outA + 0.001f);
        float outG = (g * finalA + eg * ea * (1f - finalA)) / (outA + 0.001f);
        float outB = (b * finalA + eb * ea * (1f - finalA)) / (outA + 0.001f);
        buffer[bufIdx] = PackRGBA(outR, outG, outB, outA);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Buffer Operations
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Simple 1:1 copy from source to destination buffer.
    /// Used by FlatRenderer to copy the left eye directly to output.
    /// </summary>
    public static void CopyBufferKernel(
        Index1D index,
        ArrayView<uint> source,
        ArrayView<uint> dest)
    {
        dest[index] = source[index];
    }

    /// <summary>
    /// Clear an RGBA buffer to a solid color.
    /// </summary>
    public static void ClearBufferKernel(
        Index1D index,
        ArrayView<uint> buffer,
        float r, float g, float b, float a)
    {
        buffer[index] = PackRGBA(r, g, b, a);
    }

    /// <summary>
    /// Alpha-composite a source buffer (overlay) onto a destination buffer.
    /// Overlay is drawn at (offsetX, offsetY) within the destination,
    /// with a global opacity multiplier.
    /// Used for compositing UI overlay onto the content frame.
    /// </summary>
    public static void CompositeKernel(
        Index1D index,
        ArrayView<uint> dest,       // destination (content frame)
        ArrayView<uint> overlay,    // source (UI overlay)
        int destW, int destH,
        int overlayW, int overlayH,
        int offsetX, int offsetY,
        float opacity)
    {
        int dx = index % destW;
        int dy = index / destW;

        // Map destination pixel to overlay pixel
        int ox = dx - offsetX;
        int oy = dy - offsetY;
        if (ox < 0 || ox >= overlayW || oy < 0 || oy >= overlayH)
            return; // outside overlay bounds — leave dest unchanged

        uint srcPx = overlay[oy * overlayW + ox];
        float sa = UnpackA(srcPx) * opacity;
        if (sa < 0.001f) return; // fully transparent — skip

        float sr = UnpackR(srcPx);
        float sg = UnpackG(srcPx);
        float sb = UnpackB(srcPx);

        uint dstPx = dest[index];
        float dr = UnpackR(dstPx);
        float dg = UnpackG(dstPx);
        float db = UnpackB(dstPx);
        float da = UnpackA(dstPx);

        float outA = sa + da * (1f - sa);
        float outR = (sr * sa + dr * da * (1f - sa)) / (outA + 0.001f);
        float outG = (sg * sa + dg * da * (1f - sa)) / (outA + 0.001f);
        float outB = (sb * sa + db * da * (1f - sa)) / (outA + 0.001f);
        dest[index] = PackRGBA(outR, outG, outB, outA);
    }

    /// <summary>
    /// Copy a source texture buffer into a region of the destination buffer,
    /// scaling via nearest-neighbor sampling. Used for blitting textures
    /// (text renders, images) into the compositing buffer.
    /// Dispatch size = dstRectW * dstRectH (rect-local indexing).
    /// </summary>
    public static void BlitScaledKernel(
        Index1D index,
        ArrayView<uint> dest,
        ArrayView<uint> source,
        int destW, int destH,
        int srcW, int srcH,
        int dstX, int dstY, int dstRectW, int dstRectH,
        float opacity)
    {
        // Rect-local index → dest pixel
        int lx = index % dstRectW;
        int ly = index / dstRectW;
        int dx = dstX + lx;
        int dy = dstY + ly;
        if (dx < 0 || dx >= destW || dy < 0 || dy >= destH) return;

        int destIdx = dy * destW + dx;

        // Map to source UV
        float u = lx / (float)dstRectW;
        float v = ly / (float)dstRectH;
        int sx = (int)(u * (srcW - 1));
        int sy = (int)(v * (srcH - 1));
        if (sx < 0) sx = 0; if (sx >= srcW) sx = srcW - 1;
        if (sy < 0) sy = 0; if (sy >= srcH) sy = srcH - 1;

        uint srcPx = source[sy * srcW + sx];
        float sa = UnpackA(srcPx) * opacity;
        if (sa < 0.001f) return;

        float sr = UnpackR(srcPx);
        float sg = UnpackG(srcPx);
        float sb = UnpackB(srcPx);

        uint dstPx = dest[destIdx];
        float dr = UnpackR(dstPx);
        float dg = UnpackG(dstPx);
        float db = UnpackB(dstPx);
        float da = UnpackA(dstPx);

        float outA = sa + da * (1f - sa);
        float outR = (sr * sa + dr * da * (1f - sa)) / (outA + 0.001f);
        float outG = (sg * sa + dg * da * (1f - sa)) / (outA + 0.001f);
        float outB = (sb * sa + db * da * (1f - sa)) / (outA + 0.001f);
        dest[destIdx] = PackRGBA(outR, outG, outB, outA);
    }

    /// <summary>
    /// Copy pixels from a source buffer with a side-by-side layout:
    /// Left eye at (0,0)→(halfW, h), right eye at (halfW,0)→(w, h).
    /// Used to pack two eye buffers into SBS output.
    /// </summary>
    public static void PackSBSKernel(
        Index1D index,
        ArrayView<uint> leftEye,
        ArrayView<uint> rightEye,
        ArrayView<uint> output,
        int eyeW, int eyeH,
        int outW, int outH)
    {
        int ox = index % outW;
        int oy = index / outW;

        int halfW = outW / 2;
        int srcX, srcY;
        uint pixel;

        if (ox < halfW)
        {
            // Left eye
            srcX = ox * eyeW / halfW;
            srcY = oy * eyeH / outH;
            if (srcX >= eyeW) srcX = eyeW - 1;
            if (srcY >= eyeH) srcY = eyeH - 1;
            pixel = leftEye[srcY * eyeW + srcX];
        }
        else
        {
            // Right eye
            srcX = (ox - halfW) * eyeW / halfW;
            srcY = oy * eyeH / outH;
            if (srcX >= eyeW) srcX = eyeW - 1;
            if (srcY >= eyeH) srcY = eyeH - 1;
            pixel = rightEye[srcY * eyeW + srcX];
        }

        output[index] = pixel;
    }

    /// <summary>
    /// Pack two eye buffers into Over-Under layout.
    /// Top half = left eye, bottom half = right eye.
    /// </summary>
    public static void PackOUKernel(
        Index1D index,
        ArrayView<uint> leftEye,
        ArrayView<uint> rightEye,
        ArrayView<uint> output,
        int eyeW, int eyeH,
        int outW, int outH)
    {
        int ox = index % outW;
        int oy = index / outW;

        int halfH = outH / 2;
        int srcX, srcY;
        uint pixel;

        if (oy < halfH)
        {
            // Left eye (top)
            srcX = ox * eyeW / outW;
            srcY = oy * eyeH / halfH;
            if (srcX >= eyeW) srcX = eyeW - 1;
            if (srcY >= eyeH) srcY = eyeH - 1;
            pixel = leftEye[srcY * eyeW + srcX];
        }
        else
        {
            // Right eye (bottom)
            srcX = ox * eyeW / outW;
            srcY = (oy - halfH) * eyeH / halfH;
            if (srcX >= eyeW) srcX = eyeW - 1;
            if (srcY >= eyeH) srcY = eyeH - 1;
            pixel = rightEye[srcY * eyeW + srcX];
        }

        output[index] = pixel;
    }

    /// <summary>
    /// Dimenco-style depth packing: left half = content, right half = grayscale depth.
    /// Layout matches Dimenco autostereoscopic display requirements.
    /// depthW/depthH are the actual depth buffer dimensions (may differ from content dims).
    /// </summary>
    public static void PackDimencoKernel(
        Index1D index,
        ArrayView<uint> content,
        ArrayView<float> depth,
        ArrayView<uint> output,
        int contentW, int contentH,
        int depthW, int depthH,
        int outW, int outH)
    {
        int ox = index % outW;
        int oy = index / outW;

        int halfW = outW / 2;
        if (ox < halfW)
        {
            // Left half: content (scaled)
            int srcX = ox * contentW / halfW;
            int srcY = oy * contentH / outH;
            if (srcX >= contentW) srcX = contentW - 1;
            if (srcY >= contentH) srcY = contentH - 1;
            output[index] = content[srcY * contentW + srcX];
        }
        else
        {
            // Right half: depth as grayscale (sample at depth buffer resolution)
            float u = (ox - halfW) / (float)halfW;
            float v = oy / (float)outH;
            int dx = (int)(u * (depthW - 1));
            int dy = (int)(v * (depthH - 1));
            if (dx < 0) dx = 0; if (dx >= depthW) dx = depthW - 1;
            if (dy < 0) dy = 0; if (dy >= depthH) dy = depthH - 1;
            float d = depth[dy * depthW + dx];
            if (d < 0f) d = 0f;
            if (d > 1f) d = 1f;
            output[index] = PackRGBA(d, d, d, 1f);
        }
    }
    /// <summary>
    /// SBS layout with no depth data: source on left half,
    /// solid gray at the given depth value on right half.
    /// Used as fallback when depth map is unavailable.
    /// </summary>
    public static void PackSBSFlatDepthKernel(
        Index1D index,
        ArrayView<uint> source,
        ArrayView<uint> output,
        float flatDepthValue,
        int sourceW, int sourceH,
        int outW, int outH)
    {
        int ox = index % outW;
        int oy = index / outW;
        int halfW = outW / 2;

        if (ox < halfW)
        {
            // Left half: source content
            int srcX = ox * sourceW / halfW;
            int srcY = oy * sourceH / outH;
            if (srcX >= sourceW) srcX = sourceW - 1;
            if (srcY >= sourceH) srcY = sourceH - 1;
            output[index] = source[srcY * sourceW + srcX];
        }
        else
        {
            // Right half: solid gray at convergence plane
            output[index] = PackRGBA(flatDepthValue, flatDepthValue, flatDepthValue, 1f);
        }
    }

    /// <summary>
    /// SBS layout: source on left half, turbo colormap depth visualization on right half.
    /// Used by Depth Preview renderer for side-by-side source + colormap display.
    /// depthW/depthH are the actual depth buffer dimensions (may differ from source dims).
    /// </summary>
    public static void PackSBSColormapKernel(
        Index1D index,
        ArrayView<uint> source,
        ArrayView<float> depth,
        ArrayView<uint> output,
        int sourceW, int sourceH,
        int depthW, int depthH,
        int outW, int outH)
    {
        int ox = index % outW;
        int oy = index / outW;
        int halfW = outW / 2;

        if (ox < halfW)
        {
            // Left half: source content
            int srcX = ox * sourceW / halfW;
            int srcY = oy * sourceH / outH;
            if (srcX >= sourceW) srcX = sourceW - 1;
            if (srcY >= sourceH) srcY = sourceH - 1;
            output[index] = source[srcY * sourceW + srcX];
        }
        else
        {
            // Right half: depth as turbo colormap (sample at depth buffer resolution)
            float u = (ox - halfW) / (float)halfW;
            float v = oy / (float)outH;
            int dx = (int)(u * (depthW - 1));
            int dy = (int)(v * (depthH - 1));
            if (dx < 0) dx = 0; if (dx >= depthW) dx = depthW - 1;
            if (dy < 0) dy = 0; if (dy >= depthH) dy = depthH - 1;

            float t = depth[dy * depthW + dx];
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            float idx = t * 31f;
            int lo = (int)idx;
            if (lo < 0) lo = 0;
            if (lo > 31) lo = 31;
            int hi = lo + 1;
            if (hi > 31) hi = 31;
            float frac = idx - lo;

            float loR = 0f, loG = 0f, loB = 0f;
            float hiR = 0f, hiG = 0f, hiB = 0f;
            TurboLUT(lo, ref loR, ref loG, ref loB);
            TurboLUT(hi, ref hiR, ref hiG, ref hiB);

            float r = loR + (hiR - loR) * frac;
            float g = loG + (hiG - loG) * frac;
            float b = loB + (hiB - loB) * frac;

            output[index] = PackRGBA(r, g, b, 1f);
        }
    }
}
