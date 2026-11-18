namespace WinScreen.Glass;

internal static class ContentShaders
{
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

    public const string Composite = """
        Texture2D<float4> source : register(t0);
        RWTexture2D<float4> output : register(u0);
        cbuffer Options : register(b0) { float4 levels; float4 mode; float4 page; };
        [numthreads(16,16,1)]
        void Composite(uint3 id : SV_DispatchThreadID) {
            uint ow, oh; output.GetDimensions(ow, oh);
            if (id.x >= ow || id.y >= oh) return;
            float3 c = source.Load(int3(id.xy,0)).rgb;
            float4 result;
            // Each pixel by itself, never by a guess at what is a picture: guesses changed from frame to frame as a
            // page scrolled, and parts of a picture flipped or went solid black.
            if (mode.x == 2 && mode.y == 1) {
                result = float4(c, 1);
            } else if (mode.x == 2) {
                // Each channel flips, which turns ClearType's coloured fringes into the right ones for light text on
                // dark. Then the colour of the few pixels across is put back: grey text's fringes cancel there, so it
                // stays crisp; coloured parts flip their brightness and keep their colour.
                float3 across = 0;
                for (int k = -2; k <= 2; k++) across += source.Load(int3(clamp(int2(id.x + k, id.y), int2(0,0), int2(ow - 1, oh - 1)), 0)).rgb;
                across /= 5;
                float3 hue = across - dot(across, float3(0.3,0.59,0.11));
                float light = dot(c, float3(0.3,0.59,0.11));
                float coloured = smoothstep(0.04, 0.12, length(hue));
                float3 flipped = saturate(lerp(1 - c + 2 * hue, c + (1 - 2 * light), coloured));
                // A white page becomes #191919 rather than black, as the filter kind does; the rest is lifted in step.
                result = float4(25.0 / 255 + flipped * (1 - 25.0 / 255), 1);
            } else {
                float away = length(c - page.rgb);
                float d = smoothstep(0.03, 0.30, away);
                float3 ink = d > 0.001 ? saturate((c - (1 - d) * page.rgb) / d) : c;
                // Text keeps a thin halo of the page colour, a few pixels wide: dark text on a light page sat right on
                // whatever was behind, often dark, and couldn't be read.
                float halo = 0;
                // Only strong contrast, as text has, makes a halo: a panel's faint border doesn't.
                for (int py = -3; py <= 3; py++) for (int px = -3; px <= 3; px++) {
                    float3 n = source.Load(int3(clamp(int2(id.xy) + int2(px, py), int2(0,0), int2(ow - 1, oh - 1)), 0)).rgb;
                    float nd = smoothstep(0.18, 0.45, length(n - page.rgb));
                    halo = max(halo, nd * saturate(1 - length(float2(px, py)) / 4.0));
                }
                float under = lerp(levels.x, max(levels.x, 0.9), smoothstep(0.0, 0.45, halo));
                float alpha = lerp(under, levels.y, d);
                result = float4(lerp(page.rgb * under, ink * levels.y, d), alpha);
            }
            output[id.xy] = result;
        }
        """;
}
