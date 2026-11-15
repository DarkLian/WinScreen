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
            bool dark = m > 0.03 && m < 0.08;
            bool near = m > 0.3, edge = false, nextToLight = false, nextToDark = false;
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                float n = mask.Load(int3(clamp(tile + int2(x,y), 0, int2(tw,th) - 1),0));
                // Near any picture; but inside one only among its own tiles, not among tiles that decide by pixel.
                if (n > 0.3) near = true;
                if (!(n > 0.75)) edge = true;
                if (!(n > 0.03 && n < 0.08)) nextToLight = true;
                if (n > 0.03 && n < 0.08) nextToDark = true;
            }
            float3 d3 = abs(c - page.rgb);
            bool differs = max(d3.r, max(d3.g, d3.b)) > 2.5 / 255;
            bool picture = (inside && !edge) || (near && !panel && differs);
            float away = length(c - page.rgb);
            float4 result;
            if (mode.x == 2) {
                // Each channel flips, which turns ClearType's coloured fringes into the right ones for light text on
                // dark. Then the colour of the few pixels across is put back: grey text's fringes cancel there, so it
                // stays crisp; red text is red there, so it stays red.
                float3 across = 0;
                for (int k = -2; k <= 2; k++) across += source.Load(int3(clamp(int2(id.x + k, id.y), int2(0,0), int2(ow - 1, oh - 1)), 0)).rgb;
                across /= 5;
                float3 hue = across - dot(across, float3(0.3,0.59,0.11));
                float light = dot(c, float3(0.3,0.59,0.11));
                // Coloured text flips its brightness and keeps its colour, as before; grey text flips by channel.
                float coloured = smoothstep(0.04, 0.12, length(hue));
                float3 flipped = saturate(lerp(1 - c + 2 * hue, c + (1 - 2 * light), coloured));
                // A part that is dark already keeps its colours; page colour spilling into it from next door still flips.
                bool keep = picture || (dark && (differs || !nextToLight));
                // Light at a dark part's edge next to light parts may be a light panel under a bar, not the bar's: it decides below.
                if (keep && !picture && nextToLight && light > 0.6) keep = false;
                // A tile that is only partly in a dark part, such as a title bar's lower edge with the page under it,
                // and white in a dark part next to the page, which may be the page spilling in or the bar's own white
                // title (the owner, 15 Nov: an active title lost its lower half): each pixel goes by what is around it,
                // so the bar's text keeps its colour and the page flips.
                // Text in a bar has the bar above and below it; the page next to a bar has it on one side only. The bar's
                // own last row, dark, has it on one side and stays.
                if (!keep && (dark || nextToDark)) {
                    uint above = 0, below = 0;
                    for (int ny = 1; ny <= 3; ny++) for (int nx = -3; nx <= 3; nx++) {
                        int2 up = clamp(int2(id.xy) + int2(nx, -ny), int2(0,0), int2(ow - 1, oh - 1));
                        int2 down = clamp(int2(id.xy) + int2(nx, ny), int2(0,0), int2(ow - 1, oh - 1));
                        if (dot(source.Load(int3(up, 0)).rgb, float3(0.3,0.59,0.11)) < 0.3) above++;
                        if (dot(source.Load(int3(down, 0)).rgb, float3(0.3,0.59,0.11)) < 0.3) below++;
                    }
                    keep = min(above, below) >= 7 || (light < 0.3 && max(above, below) >= 14);
                }
                // A white page becomes #191919 rather than black, as the filter kind does; the rest is lifted in step.
                flipped = 25.0 / 255 + flipped * (1 - 25.0 / 255);
                result = float4(keep ? c : flipped, 1);
            } else if (picture) {
                result = float4(c * levels.z, levels.z);
            } else {
                float d = smoothstep(0.03, 0.30, away);
                float3 ink = d > 0.001 ? saturate((c - (1 - d) * page.rgb) / d) : c;
                // Text keeps a thin halo of the page colour, a few pixels wide: dark text on a light page sat right on
                // whatever was behind, often dark, and couldn't be read (the owner, 15 Nov). The rest of the background
                // fades as chosen.
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
