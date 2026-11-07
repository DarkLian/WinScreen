namespace WinScreen.Pip;

internal static class PipShaders
{
    // Same coverage integration in CS5 and PS4. Integer loads give exact edges at 1:1.
    internal const string Source = """
        Texture2D<float4> sourceTexture : register(t0);
        SamplerState linearSampler : register(s0);
        cbuffer Geometry : register(b0) {
            float4 crop; float4 viewport; float4 surface;
            float4 selection; float4 options;
        };
        float4 area(float2 pixel, float2 outputSize) {
            float2 step = crop.zw / outputSize;
            float2 lo = crop.xy + pixel * step;
            float2 hi = min(crop.xy + crop.zw, lo + step);
            if (options.y == 2 || any(step < 1)) {
                float2 uv = (lo + hi) * 0.5 / surface.xy;
                return sourceTexture.SampleLevel(linearSampler, uv, 0);
            }
            if (options.y == 1 || all(step == 1)) return sourceTexture.Load(int3(int2((lo+hi)*0.5),0));
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
