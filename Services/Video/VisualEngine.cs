using System;
using System.IO;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace SLSKDONET.Services.Video;

/// <summary>
/// Renders audio-reactive visual primitives into a <see cref="SKBitmap"/>
/// from a <see cref="VisualFrame"/> snapshot.  Implements Issue 5.1 / #35.
/// </summary>
public sealed class VisualEngine
{
    // ── Configuration ────────────────────────────────────────────────────

    public VisualPreset Preset { get; set; } = VisualPreset.Bars;

    /// <summary>Rendered frame width in pixels.</summary>
    public int Width  { get; set; } = 1920;

    /// <summary>Rendered frame height in pixels.</summary>
    public int Height { get; set; } = 1080;

    /// <summary>Base hue (0–360) used by colour mapping.</summary>
    public float BaseHue { get; set; } = 200f;

    // ── Task 8.5: GLSL / SkSL shader ─────────────────────────────────────

    private SKRuntimeEffect? _runtimeEffect;
    private string? _loadedShaderPath;

    /// <summary>
    /// Loads a Shadertoy-compatible GLSL fragment shader from disk and
    /// compiles it via SkiaSharp's <see cref="SKRuntimeEffect"/> (SkSL).
    ///
    /// The shader may use the following uniforms injected by Orbit:
    /// <list type="bullet">
    ///   <item><c>uniform float2 iResolution</c> — render resolution in pixels</item>
    ///   <item><c>uniform float  iTime</c>        — session time in seconds (progress × total)</item>
    ///   <item><c>uniform float  iEnergy</c>       — 0–1 overall energy</item>
    ///   <item><c>uniform float  iBeatPulse</c>    — 0–1 beat strength</item>
    ///   <item><c>uniform float  iBpm</c>          — current BPM</item>
    ///   <item><c>uniform float  iBand0</c> … <c>iBand5</c> — per-band magnitudes</item>
    /// </list>
    ///
    /// Shadertoy entry point <c>void mainImage(out vec4 fragColor, in vec2 fragCoord)</c>
    /// is automatically translated to SkSL's <c>half4 main(float2 fragCoord)</c>.
    /// </summary>
    /// <returns>null if compilation succeeds; error string on failure.</returns>
    public string? LoadGlslShader(string filePath)
    {
        if (!File.Exists(filePath))
            return $"Shader file not found: {filePath}";

        string source = File.ReadAllText(filePath);
        string sksl   = TranslateToSkSl(source);

        _runtimeEffect?.Dispose();
        _runtimeEffect = null;

        _runtimeEffect = SKRuntimeEffect.Create(sksl, out string? error);
        if (_runtimeEffect is null)
            return $"SkSL compile error: {error}";

        _loadedShaderPath = filePath;
        Preset = VisualPreset.CustomGlsl;
        return null; // success
    }

    /// <summary>
    /// Translates a Shadertoy-style GLSL entry point and basic types to SkSL.
    /// </summary>
    private static string TranslateToSkSl(string glsl)
    {
        // Orbit uniform header — injected at the top of every shader
        const string UniformBlock = """
            uniform float2 iResolution;
            uniform float  iTime;
            uniform float  iEnergy;
            uniform float  iBeatPulse;
            uniform float  iBpm;
            uniform float  iBand0;
            uniform float  iBand1;
            uniform float  iBand2;
            uniform float  iBand3;
            uniform float  iBand4;
            uniform float  iBand5;
            """;

        // Replace Shadertoy entry point signature:
        //   void mainImage( out vec4 fragColor, in vec2 fragCoord )
        // →  half4 main(float2 fragCoord)
        glsl = Regex.Replace(
            glsl,
            @"void\s+mainImage\s*\(\s*out\s+vec4\s+(\w+)\s*,\s*in\s+vec2\s+(\w+)\s*\)",
            m => $"half4 main(float2 {m.Groups[2].Value})",
            RegexOptions.IgnoreCase);

        // Replace return fragColor = expr  →  return half4(expr)
        // (actually: replace "fragColor = " assignments with a helper if needed)
        // Simple pattern: rename the out variable to a local
        // For minimal compatibility we wrap the body: insert "half4 fragColor;"
        // before first brace after main( and append "return fragColor;" before
        // the matching closing brace. Use a pragmatic approach: inject at start
        // of function body.
        glsl = Regex.Replace(
            glsl,
            @"(half4 main\([^)]*\)\s*\{)",
            "$1\nhalf4 fragColor = half4(0.0);");

        // Ensure a return statement exists — append before closing brace of main
        // This is a best-effort translation; complex shaders may need manual tweaks.
        // The closing brace heuristic: last } in the file
        int lastBrace = glsl.LastIndexOf('}');
        if (lastBrace >= 0)
            glsl = glsl[..lastBrace] + "\nreturn fragColor;\n}";

        // Type name mapping: vec → float, mat → float
        glsl = glsl
            .Replace("vec2", "float2")
            .Replace("vec3", "float3")
            .Replace("vec4", "float4")
            .Replace("mat2", "float2x2")
            .Replace("mat3", "float3x3")
            .Replace("mat4", "float4x4")
            .Replace("mix(",  "mix(")   // SkSL uses mix() — same name
            .Replace("fract(","fract(") // same
            .Replace("mod(",  "mod(");  // same

        return UniformBlock + "\n" + glsl;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Renders a single frame for the supplied <paramref name="frame"/> snapshot.
    /// The caller owns the returned <see cref="SKBitmap"/> and must dispose it.
    /// </summary>
    public SKBitmap RenderFrame(VisualFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bmp    = new SKBitmap(Math.Max(1, Width), Math.Max(1, Height));
        using var canvas = new SKCanvas(bmp);

        canvas.Clear(SKColors.Black);

        switch (Preset)
        {
            case VisualPreset.Bars:       DrawBars(canvas, frame);       break;
            case VisualPreset.Circles:    DrawCircles(canvas, frame);    break;
            case VisualPreset.Waveform:   DrawWaveform(canvas, frame);   break;
            case VisualPreset.Particles:  DrawParticles(canvas, frame);  break;
            case VisualPreset.CustomGlsl: DrawCustomGlsl(canvas, frame); break;
        }

        return bmp;
    }

    /// <summary>
    /// Maps a <paramref name="frame"/> to a <see cref="VisualState"/> describing
    /// derived rendering parameters.  This is the pure-logic path covered by unit tests.
    /// </summary>
    public VisualState ComputeState(VisualFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        float energy  = Math.Clamp(frame.Energy, 0f, 1f);
        float pulse   = Math.Clamp(frame.BeatPulse, 0f, 1f);

        // Hue shifts with progress; saturation driven by energy
        float hue        = (BaseHue + frame.Progress * 120f) % 360f;
        float saturation = 0.4f + energy * 0.6f;
        float brightness = 0.4f + pulse  * 0.6f;

        // Scale: 0.5 at silence, up to 1.5 on full beat + energy
        float scale = 0.5f + energy * 0.5f + pulse * 0.5f;

        // Motion speed proportional to BPM (normalised at 120 bpm = 1.0)
        float motionSpeed = frame.Bpm > 0f ? frame.Bpm / 120f : 1f;

        return new VisualState
        {
            Hue          = hue,
            Saturation   = saturation,
            Brightness   = brightness,
            Scale        = Math.Clamp(scale, 0f, 2f),
            MotionSpeed  = Math.Clamp(motionSpeed, 0f, 4f),
            PrimaryColor = HsvToSKColor(hue, saturation, brightness),
        };
    }

    // ── Visual primitives ────────────────────────────────────────────────

    private void DrawBars(SKCanvas canvas, VisualFrame frame)
    {
        var state  = ComputeState(frame);
        int bands  = frame.FrequencyBands.Length;
        if (bands == 0) return;

        float barW = (float)Width / bands;

        using var paint = new SKPaint { IsAntialias = true, Color = state.PrimaryColor };

        for (int i = 0; i < bands; i++)
        {
            float magnitude = Math.Clamp(frame.FrequencyBands[i], 0f, 1f) * state.Scale;
            float barH      = magnitude * Height;
            float x         = i * barW;
            float y         = Height - barH;

            // Per-band hue shift
            float hue = (state.Hue + i * (360f / bands)) % 360f;
            paint.Color = HsvToSKColor(hue, state.Saturation, state.Brightness);

            canvas.DrawRect(x, y, barW - 2, barH, paint);
        }
    }

    private void DrawCircles(SKCanvas canvas, VisualFrame frame)
    {
        var state = ComputeState(frame);
        float cx  = Width  / 2f;
        float cy  = Height / 2f;

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        int rings = Math.Min(frame.FrequencyBands.Length, 6);
        for (int i = 0; i < rings; i++)
        {
            float magnitude = Math.Clamp(frame.FrequencyBands[i], 0f, 1f);
            float radius    = (i + 1) * (Height / (2f * rings)) * magnitude * state.Scale;
            float strokeW   = 2f + magnitude * 8f;

            float hue = (state.Hue + i * 40f) % 360f;
            paint.Color       = HsvToSKColor(hue, state.Saturation, state.Brightness);
            paint.StrokeWidth = strokeW;

            canvas.DrawCircle(cx, cy, Math.Max(1f, radius), paint);
        }
    }

    private void DrawWaveform(SKCanvas canvas, VisualFrame frame)
    {
        var state  = ComputeState(frame);
        int bands  = frame.FrequencyBands.Length;
        if (bands < 2) return;

        float cx = Width / 2f;
        float cy = Height / 2f;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Color       = state.PrimaryColor,
        };

        float stepX = (float)Width / (bands - 1);

        using var path = new SKPath();
        for (int i = 0; i < bands; i++)
        {
            float mag = Math.Clamp(frame.FrequencyBands[i], 0f, 1f);
            float x   = i * stepX;
            // Alternate above/below centre for waveform look
            float y   = cy + (i % 2 == 0 ? -1f : 1f) * mag * cy * state.Scale;

            if (i == 0) path.MoveTo(x, y);
            else        path.LineTo(x, y);
        }

        canvas.DrawPath(path, paint);
    }

    private void DrawParticles(SKCanvas canvas, VisualFrame frame)
    {
        var   state    = ComputeState(frame);
        float energy   = Math.Clamp(frame.Energy, 0f, 1f);
        int   count    = (int)(energy * 80f + 10f);

        // Deterministic pseudo-random positions seeded by progress
        var   rng      = new System.Random((int)(frame.Progress * 10000f));

        using var paint = new SKPaint { IsAntialias = true };

        for (int i = 0; i < count; i++)
        {
            float x     = (float)(rng.NextDouble() * Width);
            float y     = (float)(rng.NextDouble() * Height);
            float r     = 2f + (float)(rng.NextDouble() * 8f * state.Scale);
            float hue   = (state.Hue + (float)(rng.NextDouble() * 60f)) % 360f;
            paint.Color = HsvToSKColor(hue, state.Saturation, state.Brightness);

            canvas.DrawCircle(x, y, r, paint);
        }
    }

    // ── Task 8.5: GLSL render path ───────────────────────────────────────

    private void DrawCustomGlsl(SKCanvas canvas, VisualFrame frame)
    {
        if (_runtimeEffect is null)
        {
            // Fallback to bars if no shader is loaded
            DrawBars(canvas, frame);
            return;
        }

        // Use progress * an assumed 5-minute mix as a rough iTime
        float iTime = frame.Progress * 300f;

        var uniforms = new SKRuntimeEffectUniforms(_runtimeEffect);
        uniforms.Add("iResolution", new[] { (float)Width, (float)Height });
        uniforms.Add("iTime",       iTime);
        uniforms.Add("iEnergy",     Math.Clamp(frame.Energy, 0f, 1f));
        uniforms.Add("iBeatPulse",  Math.Clamp(frame.BeatPulse, 0f, 1f));
        uniforms.Add("iBpm",        frame.Bpm);

        for (int i = 0; i < 6; i++)
        {
            float v = i < frame.FrequencyBands.Length
                ? Math.Clamp(frame.FrequencyBands[i], 0f, 1f)
                : 0f;
            uniforms.Add($"iBand{i}", v);
        }

        using var shader = _runtimeEffect.ToShader(false, uniforms);
        using var paint  = new SKPaint { Shader = shader };
        canvas.DrawRect(SKRect.Create(Width, Height), paint);
    }

    // ── Colour helpers ────────────────────────────────────────────────────

    private static SKColor HsvToSKColor(float h, float s, float v)
    {
        h = h % 360f;
        if (h < 0) h += 360f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        float c  = v * s;
        float x  = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
        float m  = v - c;

        float r, g, b;
        if      (h < 60)  { r = c;  g = x;  b = 0f; }
        else if (h < 120) { r = x;  g = c;  b = 0f; }
        else if (h < 180) { r = 0f; g = c;  b = x;  }
        else if (h < 240) { r = 0f; g = x;  b = c;  }
        else if (h < 300) { r = x;  g = 0f; b = c;  }
        else              { r = c;  g = 0f; b = x;  }

        return new SKColor(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }
}

/// <summary>
/// Derived rendering parameters computed from a <see cref="VisualFrame"/>.
/// Pure data structure – no SkiaSharp dependency – making it unit-testable.
/// </summary>
public sealed class VisualState
{
    public float   Hue          { get; init; }
    public float   Saturation   { get; init; }
    public float   Brightness   { get; init; }
    public float   Scale        { get; init; }
    public float   MotionSpeed  { get; init; }
    public SKColor PrimaryColor { get; init; }
}
