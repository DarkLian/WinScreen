namespace WinScreen.Pip;

internal static class PipShaders
{
    // Same coverage integration in CS5 and PS4. Integer loads give exact edges at 1:1.
    // options.y is the scaling (PipScaling): 0 Auto, 1 Crisp (nearest texel when enlarging), 2 Smooth (linear), 3 Auto without
    // the cubic filters (Windows' software renderer, where every extra texel read is the processor's).
    internal const string Source = """
        Texture2D<float4> sourceTexture : register(t0);
        SamplerState linearSampler : register(s0);
        cbuffer Geometry : register(b0) {
            float4 crop; float4 viewport; float4 surface;
            float4 selection; float4 options;
            // orient: quarter turns clockwise, mirrored left to right (1), and the upright picture's size.
            float4 orient;
        };
        // Reads stay inside the crop, whose edge texels repeat beyond it as the frame's do: what lies outside a crop
        // never bleeds into its edge.
        float4 texel(int2 p) { return sourceTexture.Load(int3(clamp(p, int2(crop.xy), int2(crop.xy + crop.zw) - 1), 0)); }
        float4 linearAt(float2 p) {
            return sourceTexture.SampleLevel(linearSampler, clamp(p, crop.xy + 0.5, crop.xy + crop.zw - 0.5) / surface.xy, 0);
        }
        float luma(float4 c) { return dot(c.rgb, float3(0.299, 0.587, 0.114)); }
        // Cubic convolution weights (a = -0.5) for the four texels around a fraction f.
        float4 cubic(float f) {
            float f2 = f * f, f3 = f2 * f;
            return float4(-0.5*f3 + f2 - 0.5*f, 1.5*f3 - 2.5*f2 + 1, -1.5*f3 + 2*f2 + 0.5*f, 0.5*f3 - 0.5*f2);
        }
        // Enlarging: a cubic interpolation kept within the four nearest texels (no halos), eased along an edge where
        // there is one (fewer stair steps on slopes and curves), then sharpened by as much as the local contrast leaves
        // room for. s is the point in the source, in texels, centres at .5.
        float4 enlarge(float2 s) {
            float2 t = s - 0.5;
            int2 i = int2(floor(t));
            float2 f = t - i;
            float4 wx = cubic(f.x), wy = cubic(f.y);
            float4 sum = 0;
            float l[4][4];
            [unroll] for (int y = 0; y < 4; y++) {
                float4 row = 0;
                [unroll] for (int x = 0; x < 4; x++) {
                    float4 c = texel(i + int2(x - 1, y - 1));
                    l[y][x] = luma(c);
                    row += c * wx[x];
                }
                sum += row * wy[y];
            }
            float4 a = texel(i), b = texel(i + int2(1,0)), c = texel(i + int2(0,1)), d = texel(i + int2(1,1));
            float4 lo = min(min(a, b), min(c, d)), hi = max(max(a, b), max(c, d));
            float4 color = clamp(sum, lo, hi);
            // The edge through the middle four texels, from the brightness around them.
            float2 g = 0;
            [unroll] for (int y = 1; y < 3; y++)
                [unroll] for (int x = 1; x < 3; x++)
                    g += float2(l[y][x+1] - l[y][x-1], l[y+1][x] - l[y-1][x]);
            float strength = saturate(length(g) * 1.5 - 0.15);
            if (strength > 0) {
                float2 along = normalize(float2(-g.y, g.x)) * 0.75;
                float4 eased = (linearAt(s + along) + linearAt(s - along)) * 0.25 + color * 0.5;
                color = clamp(lerp(color, eased, strength * 0.5), lo, hi);
            }
            // Sharpen against the linear blend. Kept within the nearest four texels, so an edge gets steeper but never
            // brighter or darker than the texels beside it: no halo.
            color = clamp(color + (color - linearAt(s)) * 1.2, lo, hi);
            return color;
        }
        float4 area(float2 pixel, float2 outputSize) {
            float2 step = crop.zw / outputSize;
            float2 lo = crop.xy + pixel * step;
            float2 hi = min(crop.xy + crop.zw, lo + step);
            // A graphics card divides through a reciprocal, so 113/113 can come out a hair under 1: that is 1:1.
            if (all(abs(step - 1) < 0.0001)) return texel(int2(floor((lo + hi) * 0.5)));
            if (any(step < 1)) {
                float2 middle = (lo + hi) * 0.5;
                if (options.y == 0) return enlarge(middle);
                if (options.y == 1) return texel(int2(floor(middle)));
                return linearAt(middle);
            }
            // Shrinking, one texel a pixel would skip whole rows and columns, more the smaller the view: Crisp averages
            // them, and so does Smooth below half size (one linear read covers only two texels across).
            if (options.y == 2 && all(step < 2)) return linearAt((lo + hi) * 0.5);
            float4 sum = 0;
            [loop] for (int y = (int)floor(lo.y); y < (int)ceil(hi.y); y++) {
                float wy = max(0, min(hi.y, y+1.0) - max(lo.y, (float)y));
                // Keep individual accumulation chains bounded by a texture dimension, even for extreme ratios.
                float4 row = 0;
                [loop] for (int x = (int)floor(lo.x); x < (int)ceil(hi.x); x++) {
                    float wx = max(0, min(hi.x, x+1.0) - max(lo.x, (float)x));
                    row += sourceTexture.Load(int3(x,y,0)) * wx;
                }
                sum += row * wy;
            }
            return sum / max(0.000001, (hi.x-lo.x)*(hi.y-lo.y));
        }
        float4 VS(uint id : SV_VertexID) : SV_Position {
            float2 p = float2((id << 1) & 2, id & 2);
            return float4(p * float2(2,-2) + float2(-1,1),0,1);
        }
        float4 PS(float4 position : SV_Position) : SV_Target {
            float2 p = position.xy;
            if (any(p < viewport.xy) || any(p >= viewport.xy + viewport.zw)) return float4(79.0/255,140.0/255,1,1);
            float2 relative = p - viewport.xy - 0.5;
            // The upright picture's pixel shown here, as ViewTurn.ToUpright works it out: whole pixels, so a turned
            // picture keeps every edge.
            float2 upright = relative, size = viewport.zw;
            if (orient.y == 1) upright.x = size.x - 1 - upright.x;
            [loop] for (int turn = 0; turn < (int)orient.x; turn++) { upright = float2(upright.y, size.x - 1 - upright.x); size = size.yx; }
            float4 color;
            if (options.z == 1) color = sourceTexture.Load(int3(int2(upright),0));
            else color = area(upright, orient.zw);
            float alpha = options.x;
            if (options.w == 1 && (any(p < selection.xy) || any(p >= selection.xy+selection.zw))) alpha *= 90.0/255;
            return float4(color.rgb * alpha,1);
        }
        #ifdef COMPUTE
        RWTexture2D<float4> outputTexture : register(u0);
        [numthreads(16,16,1)]
        void CS(uint3 id : SV_DispatchThreadID) {
            if (all(id.xy < (uint2)orient.zw)) outputTexture[id.xy] = area(id.xy, orient.zw);
        }
        // Auto shrinking a little (steps 1 to 1.5; below that the even average does as well): the cubic of enlarge(),
        // stretched over the step, keeps strokes crisper than an even average, and is kept within the texels the pixel
        // covers, so no halo. Taps stay inside the crop. Two passes through the group's memory, across then down, read
        // each texel about twice instead of some thirty times: 16 rows of pixels need at most 31 rows of texels.
        float catrom(float t) { return t < 1 ? (1.5*t - 2.5)*t*t + 1 : t < 2 ? ((-0.5*t + 2.5)*t - 4)*t + 2 : 0; }
        groupshared float4 across[32][16];
        [numthreads(16,16,1)]
        void CSCubic(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID) {
            float2 step = crop.zw / orient.zw;
            int2 start = int2(crop.xy), end = int2(crop.xy + crop.zw);
            float2 c = crop.xy + (id.xy + 0.5) * step;
            int firstX = max((int)floor(c.x - 2 * step.x), start.x), lastX = min((int)ceil(c.x + 2 * step.x), end.x);
            int rows = max((int)floor(crop.y + (group.y * 16 + 0.5) * step.y - 2 * step.y), start.y);
            int rowsEnd = min(min((int)ceil(crop.y + (group.y * 16 + 15.5) * step.y + 2 * step.y), end.y), rows + 32);
            [loop] for (int r = rows + (int)local.y; r < rowsEnd; r += 16) {
                float4 sum = 0;
                float total = 0;
                [loop] for (int x = firstX; x < lastX; x++) {
                    float w = catrom(abs(x + 0.5 - c.x) / step.x);
                    sum += sourceTexture.Load(int3(x, r, 0)) * w;
                    total += w;
                }
                across[r - rows][local.x] = sum / max(total, 0.000001);
            }
            GroupMemoryBarrierWithGroupSync();
            if (any(id.xy >= (uint2)orient.zw)) return;
            int firstY = max((int)floor(c.y - 2 * step.y), rows), lastY = min((int)ceil(c.y + 2 * step.y), rowsEnd);
            float4 sum = 0;
            float total = 0;
            [loop] for (int y = firstY; y < lastY; y++) {
                float w = catrom(abs(y + 0.5 - c.y) / step.y);
                sum += across[y - rows][local.x] * w;
                total += w;
            }
            float2 lo = crop.xy + id.xy * step, hi = min(crop.xy + crop.zw, lo + step);
            float4 low = 1, high = 0;
            [loop] for (int ty = (int)floor(lo.y); ty < (int)ceil(hi.y); ty++)
                [loop] for (int tx = (int)floor(lo.x); tx < (int)ceil(hi.x); tx++) {
                    float4 t = sourceTexture.Load(int3(tx, ty, 0));
                    low = min(low, t);
                    high = max(high, t);
                }
            outputTexture[id.xy] = clamp(sum / total, low, high);
        }
        #endif
        """;
}
