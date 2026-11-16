namespace WinScreen.Pip;

internal static class PipShaders
{
    // Same coverage integration in CS5 and PS4. Integer loads give exact edges at 1:1.
    // options.y is the scaling: 0 Auto, 1 Sharp (nearest texel), 2 Smooth (linear), 3 Auto without the enlarging
    // filter (Windows' software renderer, where every extra texel read is the processor's).
    internal const string Source = """
        Texture2D<float4> sourceTexture : register(t0);
        SamplerState linearSampler : register(s0);
        cbuffer Geometry : register(b0) {
            float4 crop; float4 viewport; float4 surface;
            float4 selection; float4 options;
        };
        float4 texel(int2 p) { return sourceTexture.Load(int3(clamp(p, int2(0,0), int2(surface.xy) - 1), 0)); }
        float4 linearAt(float2 p) { return sourceTexture.SampleLevel(linearSampler, p / surface.xy, 0); }
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
            if (options.y == 1) return texel(int2(floor((lo + hi) * 0.5)));
            // A graphics card divides through a reciprocal, so 113/113 can come out a hair under 1: that is 1:1.
            if (all(abs(step - 1) < 0.0001)) return texel(int2(floor((lo + hi) * 0.5)));
            if (any(step < 1)) {
                float2 middle = (lo + hi) * 0.5;
                if (options.y == 0) return enlarge(middle);
                return linearAt(middle);
            }
            if (options.y == 2) return linearAt((lo + hi) * 0.5);
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
            float4 color;
            if (options.z == 1) color = sourceTexture.Load(int3(int2(relative),0));
            else color = area(relative, viewport.zw);
            float alpha = options.x;
            if (options.w == 1 && (any(p < selection.xy) || any(p >= selection.xy+selection.zw))) alpha *= 90.0/255;
            return float4(color.rgb * alpha,1);
        }
        #ifdef COMPUTE
        RWTexture2D<float4> outputTexture : register(u0);
        [numthreads(16,16,1)]
        void CS(uint3 id : SV_DispatchThreadID) {
            if (all(id.xy < (uint2)viewport.zw)) outputTexture[id.xy] = area(id.xy, viewport.zw);
        }
        #endif
        """;
}
