namespace RuApp.Controls;

/// <summary>
/// The black hole, as one full-screen HLSL pixel shader run every frame on the GPU.
/// <para>
/// Each pixel traces a light ray from a camera falling toward the hole through Schwarzschild
/// spacetime (M = 1; in the ray's plane u'' = 3u² − u, u = 1/r). Far rays use the weak-field
/// bending 4M/b. A ray that falls in is the shadow; one that skims r = 3 lights the photon ring;
/// one that passes through the flared accretion slab gathers glowing gas on the way (filaments
/// sheared by Keplerian rotation, relativistic beaming, plasma flares); one that escapes looks up
/// the "sky", which is a snapshot of the window itself. So the real interface is lensed around
/// the hole into an Einstein ring, spirals in and is swallowed, and the explosion's shockwave
/// refracts it and puts it back.
/// </para>
/// </summary>
internal static class BlackHoleShader
{
    public const string Source = """
cbuffer Scene : register(b0)
{
    float2 Resolution; float Time; float CameraDistance;
    float2 Center; float TanHalf; float Disk;
    float Swirl; float Pull; float Phase; float Flash;
    float Shock; float Heat; float HoleOn; float ShadowPx;
    float2 ShakeOffset; float MaxShock; float Fade;
};

Texture2D Ui : register(t0);
SamplerState LinearClamp : register(s0);

struct VsOut { float4 Position : SV_Position; };

VsOut VSMain(uint id : SV_VertexID)
{
    VsOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.Position = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

static const float Elevation = 0.2;
static const float Roll = -0.26;
static const float InnerRadius = 3.15;
static const float Pi = 3.14159265;

float Hash31(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float Noise3(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3 - 2 * f);
    return lerp(
        lerp(lerp(Hash31(i), Hash31(i + float3(1, 0, 0)), f.x),
             lerp(Hash31(i + float3(0, 1, 0)), Hash31(i + float3(1, 1, 0)), f.x), f.y),
        lerp(lerp(Hash31(i + float3(0, 0, 1)), Hash31(i + float3(1, 0, 1)), f.x),
             lerp(Hash31(i + float3(0, 1, 1)), Hash31(i + float3(1, 1, 1)), f.x), f.y), f.z);
}

// Gas filaments: many cells across the radius, few around the orbit, so they are long fibres.
float Fibres(float r, float ang, float level)
{
    float3 p = float3(r * 2.4, cos(ang) * 1.7, sin(ang) * 1.7) + level * 0.45;
    float n = 0, a = 0.5;
    [unroll] for (int o = 0; o < 4; o++)
    {
        float v = Noise3(p);
        n += a * (1 - abs(2 * v - 1));
        p = p * float3(2.3, 1.55, 1.55) + 7.13;
        a *= 0.55;
    }
    return saturate((n - 0.38) * 2.0);
}

float3 Ramp(float heat)
{
    float3 c = lerp(float3(0.32, 0.07, 0.03), float3(0.96, 0.42, 0.2), saturate(heat * 2.0));
    c = lerp(c, float3(1.0, 0.72, 0.5), saturate(heat * 1.4 - 0.45));
    c = lerp(c, float3(1.0, 0.94, 0.86), saturate(heat * 1.1 - 0.95));
    return c;
}

float3 Filmic(float3 x)
{
    return saturate(x * (2.51 * x + 0.03) / (x * (2.43 * x + 0.59) + 0.14));
}

float3 Space(float3 d)
{
    float3 g = floor(d * 260);
    float h = Hash31(g);
    float star = h > 0.9965 ? pow(Hash31(g + 3.1), 6) * 1.6 : 0;
    return float3(0.014, 0.009, 0.008) + star * float3(0.9, 0.92, 1.0);
}

// The sky is the window: a direction becomes a pixel of the snapshot, after the suction warp.
float3 Sky(float3 d, float3 camF, float3 camR, float3 camU)
{
    float z = dot(d, camF);
    if (z <= 0.05) return Space(d);
    float2 s = float2(dot(d, camR), dot(d, camU)) / z / TanHalf;
    float2 q = float2(s.x, -s.y) * Resolution.y * 0.5;
    float rr = length(q) + 1e-3;

    // Behind the shockwave the window is whole again.
    float restore = Shock >= 0 ? smoothstep(Shock - 180, Shock + 30, rr) : 1;
    float swirl = Swirl * restore * exp(-rr / (Resolution.y * 0.38));
    float sn, cs;
    sincos(swirl, sn, cs);
    q = float2(cs * q.x - sn * q.y, sn * q.x + cs * q.y);
    q *= 1 + Pull * restore * (Resolution.y * 0.42) / (rr + 24);
    if (Shock >= 0)
    {
        float w = rr - Shock;
        q += q / rr * 34 * exp(-w * w / 5200) * (1 - Shock / MaxShock);
    }

    float2 uv = (Center + q) / Resolution;
    if (any(uv < 0) || any(uv > 1)) return Space(d);
    float3 ui = Ui.SampleLevel(LinearClamp, uv, 0).rgb;
    float edge = smoothstep(0, 0.006, min(min(uv.x, uv.y), min(1 - uv.x, 1 - uv.y)));
    return lerp(Space(d), ui, edge);
}

struct SceneOut
{
    float4 Background : SV_Target0; // the lensed window, plain colour
    float4 Light : SV_Target1;      // everything that glows, in HDR, for bloom and tone mapping
};

SceneOut PSScene(VsOut input)
{
    float2 frag = input.Position.xy + ShakeOffset;
    float3 camPos = float3(0, sin(Elevation), cos(Elevation)) * CameraDistance;
    float3 f = -normalize(camPos);
    float3 right0 = normalize(cross(f, float3(0, 1, 0)));
    float3 up0 = cross(right0, f);
    float sr, cr;
    sincos(Roll, sr, cr);
    float3 camR = cr * right0 + sr * up0;
    float3 camU = cr * up0 - sr * right0;

    float2 p = (frag - Center) / (Resolution.y * 0.5);
    float3 d = normalize(f + p.x * TanHalf * camR - p.y * TanHalf * camU);

    float3 emission = 0;
    float transmit = 1;
    bool captured = false;
    float3 dOut = d;
    float ring = 0;

    if (HoleOn > 0.5)
    {
        float3 e1 = normalize(camPos);
        float along = dot(d, e1);
        float3 perp = d - along * e1;
        float side = length(perp);
        float b = side * CameraDistance;
        if (side < 1e-6)
        {
            captured = true;
        }
        else if (b > 48)
        {
            // Weak field: bend toward the hole by 4M/b (+ second order).
            float3 e2 = perp / side;
            float3 n = normalize(e2 - dot(e2, d) * d);
            float bend = 4 / b + 11.78 / (b * b);
            dOut = normalize(cos(bend) * d - sin(bend) * n);
        }
        else
        {
            float3 e2 = perp / side;
            float u = 1 / CameraDistance;
            float v = -along / (side * CameraDistance);
            float phi = 0;
            float rmin = CameraDistance;
            float rOut = 5.5 + 15 * Disk;
            [loop] for (int s = 0; s < 190; s++)
            {
                float h = u > 0.2 ? 0.012 : u > 0.1 ? 0.022 : u > 0.045 ? 0.04 : 0.075;
                float mu = u + 0.5 * h * v;
                float mv = v + 0.5 * h * (3 * u * u - u);
                u += h * mv;
                v += h * (3 * mu * mu - mu);
                phi += h;
                if (u >= 0.5) { captured = true; break; }
                float rad = 1 / u;
                rmin = min(rmin, rad);
                if (v < 0 && rad > CameraDistance * 1.3 + 40) break;
                if (phi > 12.5) { captured = true; break; }

                if (Disk > 0.001 && rad < rOut && rad > InnerRadius)
                {
                    float sn, cs;
                    sincos(phi, sn, cs);
                    float3 outward = cs * e1 + sn * e2;
                    float3 P = rad * outward;
                    float H = 0.12 + 0.048 * rad;
                    float level = P.y / H;
                    if (abs(level) < 1)
                    {
                        float3 around = -sn * e1 + cs * e2;
                        float drdphi = -v / (u * u);
                        float3 tangent = normalize(drdphi * outward + rad * around);
                        float ds = h * sqrt(drdphi * drdphi + rad * rad);

                        float theta = atan2(P.z, P.x);
                        float omega = 1.3 * pow(InnerRadius / rad, 1.5);
                        float ang = theta - Phase * omega;
                        float fib = Fibres(rad, ang, level);
                        float profile = pow(InnerRadius / rad, 1.25)
                            * smoothstep(InnerRadius, InnerRadius + 0.6, rad)
                            * (1 - smoothstep(rOut * 0.42, rOut, rad));

                        // Relativistic beaming: the side moving toward us is far brighter.
                        float speed = min(0.6, sqrt(1 / max(rad - 1.5, 1)));
                        float3 velocity = float3(-sin(theta), 0, cos(theta));
                        float doppler = sqrt(1 - speed * speed) / (1 - speed * dot(velocity, -tangent));
                        float boost = clamp(pow(doppler, 2.0), 0.18, 3.0);

                        float dens = Disk * profile * exp(-2.6 * level * level) * (0.22 + 1.5 * fib);
                        float heat = dens * (1.0 + Heat * 0.9) * boost;
                        float3 glow = Ramp(heat * 0.85) * heat * 2.1;

                        // Blue-white plasma flares riding the inner disk.
                        [unroll] for (int k = 0; k < 3; k++)
                        {
                            float fr = 3.9 + k * 0.85;
                            float fa = 2.4 + k * 2.1 - Phase * 1.3 * pow(InnerRadius / fr, 1.5);
                            float da = theta - fa;
                            da -= 2 * Pi * round(da / (2 * Pi));
                            float dr = (rad - fr) / 0.5;
                            float across = da * rad / 0.75;
                            float g = exp(-dr * dr - across * across) * Disk
                                * (0.55 + 0.45 * sin(Time * (5.3 + k * 1.7) + k)) * (0.5 + fib);
                            glow += float3(0.5, 0.72, 1.35) * g * 3.2 + g * g * 2.5;
                        }

                        emission += transmit * glow * ds * 0.85;
                        transmit *= exp(-dens * ds * 1.15);
                        if (transmit < 0.01) break;
                    }
                }
            }

            float sn, cs;
            sincos(phi, sn, cs);
            float3 outward = cs * e1 + sn * e2;
            float3 around = -sn * e1 + cs * e2;
            dOut = normalize((-v / (u * u)) * outward + (1 / u) * around);
            ring = rmin >= 3 ? exp(-(rmin - 3) / 0.05) : 0;
        }
    }

    float3 bg = captured ? 0 : Sky(dOut, f, camR, camU);
    // The window darkens and reddens as the hole grows and feeds.
    bg *= lerp(float3(1, 1, 1), float3(0.55, 0.36, 0.28), saturate(Disk * 0.9 + Heat * 0.3));
    float3 light = emission * 0.8;

    if (HoleOn > 0.5)
    {
        light += float3(1.0, 0.9, 0.78) * ring * 1.6 * transmit;
        float rr = length(frag - Center);
        float halo = exp(-max(rr - ShadowPx, 0) / (ShadowPx * 1.1 + 1)) * (0.08 + 0.3 * Heat) * Disk;
        // Glow around the hole, never inside its shadow.
        light += float3(1.0, 0.5, 0.26) * halo * (captured ? 0 : 1) * smoothstep(ShadowPx * 0.95, ShadowPx * 1.2, rr);
    }

    if (Shock >= 0)
    {
        float rr = length(frag - Center);
        float fade = saturate(1 - Shock / MaxShock);
        float w = rr - Shock;
        light += float3(1.0, 0.86, 0.72) * exp(-abs(w) / 10) * fade * 2.2;
        light += float3(0.42, 0.58, 1.0) * exp(-abs(w + 46) / 30) * fade * 0.8;
        // Sparks: radial streaks thrown out behind the front.
        float a = atan2(frag.y - Center.y, frag.x - Center.x);
        float cell = (a + Pi) / (2 * Pi) * 260;
        float bin = floor(cell);
        float lane = 1 - abs(frac(cell) - 0.5) * 2;
        float reach = Shock * (0.45 + 0.7 * Hash31(float3(bin, 1, 2)));
        float streak = exp(-abs(rr - reach) / 7) * step(0.5, Hash31(float3(bin, 3, 4))) * smoothstep(0.55, 1, lane);
        light += float3(1.0, 0.78, 0.55) * streak * fade * 2.4;
    }

    SceneOut o;
    o.Background = float4(bg * transmit, 1);
    // Alpha marks the shadow, so the composite can keep bloom out of it.
    o.Light = float4(light, captured ? 1 : 0);
    return o;
}

// ── Bloom ────────────────────────────────────────────────────────────────

cbuffer BloomPass : register(b1)
{
    float2 Direction; float2 SourceTexel;
    float2 TargetTexel; float2 PassPad;
};

Texture2D Source : register(t1);

// 4× downsample: four bilinear taps cover a 4×4 block.
float4 PSDown(VsOut input) : SV_Target
{
    float2 uv = input.Position.xy * TargetTexel;
    float3 sum = 0;
    sum += Source.SampleLevel(LinearClamp, uv + SourceTexel * float2(-1, -1), 0).rgb;
    sum += Source.SampleLevel(LinearClamp, uv + SourceTexel * float2(1, -1), 0).rgb;
    sum += Source.SampleLevel(LinearClamp, uv + SourceTexel * float2(-1, 1), 0).rgb;
    sum += Source.SampleLevel(LinearClamp, uv + SourceTexel * float2(1, 1), 0).rgb;
    return float4(sum * 0.25, 1);
}

// Separable Gaussian, 9 bilinear taps (≈17 texels) along Direction.
float4 PSBlur(VsOut input) : SV_Target
{
    float2 uv = input.Position.xy * TargetTexel;
    static const float weights[5] = { 0.2270270, 0.3162162, 0.0702703, 0.0315315, 0.0081081 };
    static const float offsets[5] = { 0.0, 1.3846154, 3.2307692, 5.0769231, 6.9230769 };
    float3 sum = Source.SampleLevel(LinearClamp, uv, 0).rgb * weights[0];
    [unroll] for (int i = 1; i < 5; i++)
    {
        float2 delta = Direction * SourceTexel * offsets[i] * 1.6;
        sum += Source.SampleLevel(LinearClamp, uv + delta, 0).rgb * weights[i];
        sum += Source.SampleLevel(LinearClamp, uv - delta, 0).rgb * weights[i];
    }
    return float4(sum, 1);
}

// ── Final image ──────────────────────────────────────────────────────────

Texture2D BackgroundTex : register(t2);
Texture2D LightTex : register(t3);
Texture2D BloomNear : register(t4);
Texture2D BloomFar : register(t5);

float4 PSComposite(VsOut input) : SV_Target
{
    float2 uv = input.Position.xy / Resolution;
    float2 fromCenter = uv - 0.5;
    // Lens: colours split toward the edges as the hole and the blast overdrive the optics.
    float split = 0.0012 + 0.006 * Heat + 0.014 * Flash + 0.004 * Disk;
    float2 shift = fromCenter * split;
    float3 light;
    light.r = LightTex.SampleLevel(LinearClamp, uv + shift, 0).r;
    light.g = LightTex.SampleLevel(LinearClamp, uv, 0).g;
    light.b = LightTex.SampleLevel(LinearClamp, uv - shift, 0).b;
    float shadow = LightTex.SampleLevel(LinearClamp, uv, 0).a;
    float3 bloom = (BloomNear.SampleLevel(LinearClamp, uv, 0).rgb * 0.55
        + BloomFar.SampleLevel(LinearClamp, uv, 0).rgb * 0.75) * (1 - 0.9 * shadow);
    float3 bg;
    float bgSplit = split * saturate(Disk + Flash);
    bg.r = BackgroundTex.SampleLevel(LinearClamp, uv + fromCenter * bgSplit, 0).r;
    bg.g = BackgroundTex.SampleLevel(LinearClamp, uv, 0).g;
    bg.b = BackgroundTex.SampleLevel(LinearClamp, uv - fromCenter * bgSplit, 0).b;

    float3 col = bg + Filmic(light + bloom);

    // Vignette and grain, only while the hole is on screen.
    float intensity = saturate(Disk + Heat * 0.5);
    float edge = length(fromCenter * float2(Resolution.x / Resolution.y, 1));
    col *= 1 - intensity * 0.45 * smoothstep(0.35, 1.05, edge);
    col += (Hash31(float3(input.Position.xy, floor(Time * 48))) - 0.5) * 0.04 * saturate(intensity + Flash);

    col = lerp(col, float3(1.0, 0.97, 0.92), Flash);
    // Fade hands back to the real window on the last frames.
    float3 ui = Ui.SampleLevel(LinearClamp, uv, 0).rgb;
    col = lerp(col, ui, Fade);
    return float4(col, 1);
}
""";
}
