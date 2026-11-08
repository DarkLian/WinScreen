namespace WinScreen.Glass;

/// <summary>The two compute passes. Statistics run on each new frame; the composite uses the picture mask.</summary>
internal static class ContentShaders
{
    /// <summary>
    /// One tile: its most common colour, how much of it is exactly that colour (CSS backgrounds and panels
    /// are; photos never are), and whether it has the spread of a picture.
    /// </summary>
    public const string Stats = """
        Texture2D<float4> source : register(t0);
        RWTexture2D<float4> tiles : register(u0);
        [numthreads(8,8,1)]
        void Stats(uint3 id : SV_DispatchThreadID) {
            uint w, h; source.GetDimensions(w, h);
            int2 origin = int2(id.xy) * 16;
            if (origin.x + 16 > (int)w || origin.y + 16 > (int)h) return;
            uint hist[32]; float3 sum[32];
            for (uint i = 0; i < 32; i++) { hist[i] = 0; sum[i] = 0; }
            uint flat = 0;
            float previous = -1;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) {
                float3 c = source.Load(int3(origin + int2(x,y), 0)).rgb;
                float l = dot(c, float3(0.3,0.59,0.11));
                uint bin = min(31, (uint)(l * 32));
                hist[bin]++; sum[bin] += c;
                if (abs(l - previous) < 0.006) flat++;
                previous = l;
            }
            uint best = 0, used = 0;
            for (uint j = 0; j < 32; j++) { if (hist[j] > hist[best]) best = j; if (hist[j] >= 3) used++; }
            bool picture = hist[best] / 256.0 < 0.42 && flat < 120 && used >= 7;
            float3 mode = sum[best] / max(1, hist[best]);
            uint exact = 0;
            for (int y2 = 0; y2 < 16; y2++) for (int x2 = 0; x2 < 16; x2++) {
                float3 e = abs(source.Load(int3(origin + int2(x2,y2), 0)).rgb - mode);
                if (max(e.r, max(e.g, e.b)) < 1.5 / 255) exact++;
            }
            tiles[id.xy] = float4(mode, (picture ? 2 : 0) + min(exact / 256.0, 0.999));
        }
        """;

    /// <summary>
    /// Glass: the page colour fades to the chosen opacity and is taken out of text, so text keeps clean
    /// edges. Pictures keep their own opacity. Dark: light and dark swap everywhere except pictures.
    /// </summary>
    public const string Composite = """
        Texture2D<float4> source : register(t0);
        Texture2D<float> mask : register(t1);
        RWTexture2D<float4> output : register(u0);
        cbuffer Options : register(b0) { float4 levels; float4 mode; float4 page; };
        [numthreads(16,16,1)]
        void Composite(uint3 id : SV_DispatchThreadID) {
            uint ow, oh; output.GetDimensions(ow, oh);
            if (id.x >= ow || id.y >= oh) return;
            float3 c = source.Load(int3(id.xy,0)).rgb;
            int2 tile = int2(id.xy) / 16;
            uint tw, th; mask.GetDimensions(tw, th);
            float m = mask.Load(int3(min(tile, int2(tw,th) - 1),0));
            bool inside = m > 0.75;
            bool panel = m > 0.1 && m < 0.3;
            bool near = m > 0.3, edge = false;
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                bool on = mask.Load(int3(clamp(tile + int2(x,y), 0, int2(tw,th) - 1),0)) > 0.3;
                if (on) near = true; else edge = true;
            }
            float3 d3 = abs(c - page.rgb);
            bool differs = max(d3.r, max(d3.g, d3.b)) > 2.5 / 255;
            bool picture = (inside && !edge) || (near && !panel && differs);
            float away = length(c - page.rgb);
            float4 result;
            if (mode.x == 2) {
                float light = dot(c, float3(0.3,0.59,0.11));
                result = float4(picture ? c : saturate(c + (1 - 2 * light)), 1);
            } else if (picture) {
                result = float4(c * levels.z, levels.z);
            } else {
                float d = smoothstep(0.03, 0.30, away);
                float alpha = lerp(levels.x, levels.y, d);
                float3 ink = d > 0.001 ? saturate((c - (1 - d) * page.rgb) / d) : c;
                result = float4(lerp(page.rgb * levels.x, ink * levels.y, d), alpha);
            }
            output[id.xy] = result;
        }
        """;
}
