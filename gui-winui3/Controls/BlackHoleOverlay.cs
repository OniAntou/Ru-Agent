using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RuApp.Controls;

/// <summary>
/// The New Chat easter egg. The window is snapshotted and handed to a GPU shader
/// (<see cref="BlackHoleShader"/>) that renders it as the sky behind a black hole: a speck
/// appears and bends the interface around itself, the camera falls toward it while the whole UI
/// spirals in and is swallowed and the accretion disk flares up, it throbs, collapses to a point,
/// explodes, and the shockwave sweeps outward and leaves the window exactly as it was.
/// Purely visual: the real controls are untouched underneath, input is held off while it plays,
/// and it is skipped when Windows animations are off or the GPU path is unavailable.
/// </summary>
public sealed class BlackHoleOverlay : Grid
{
    // Timeline, in seconds.
    private const float FormedAt = 1.0f;
    private const float CriticalAt = 4.4f;
    private const float ImplodeAt = 5.05f;
    private const float ExplodeAt = 5.3f;
    private const float ShockSeconds = 1.5f;
    private const float Total = 7.4f;

    private const float TanHalf = 0.4663f; // tan(25°): a 50° vertical field of view
    private const float StartDistance = 6000f;
    private const float ApproachDistance = 240f;
    private const float ClosestDistance = 20f;

    public BlackHoleOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    public bool IsPlaying { get; private set; }

    /// <summary>Starts compiling the shaders early, so a burst of clicks finds them ready.</summary>
    public static void Prepare() => _ = BlackHoleRenderer.CompileAsync();

    public async Task PlayAsync(FrameworkElement scene)
    {
        if (IsPlaying || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        IsPlaying = true;
        BlackHoleRenderer? renderer = null;
        try
        {
            // Let a New Chat navigation from the same burst settle before the window is captured.
            await Task.Delay(250);
            var shaders = await BlackHoleRenderer.CompileAsync();
            var capture = new RenderTargetBitmap();
            await capture.RenderAsync(scene);
            var snapshot = (await capture.GetPixelsAsync()).ToArray();
            if (capture.PixelWidth < 64 || capture.PixelHeight < 64) return;

            var scale = (float)(scene.XamlRoot?.RasterizationScale ?? 1.0);
            var width = Math.Max(64, (int)Math.Round(scene.ActualWidth * scale));
            var height = Math.Max(64, (int)Math.Round(scene.ActualHeight * scale));

            var panel = new SwapChainPanel();
            var loaded = new TaskCompletionSource();
            panel.Loaded += (_, _) => loaded.TrySetResult();
            Background = new SolidColorBrush(Colors.Transparent); // swallow clicks while it plays
            IsHitTestVisible = true;
            Visibility = Visibility.Visible;
            Children.Add(panel);
            await loaded.Task;

            renderer = new BlackHoleRenderer(panel, shaders, snapshot, capture.PixelWidth, capture.PixelHeight, width, height, scale, scale);
            await RunAsync(renderer);
        }
        catch (Exception error) when (error is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            // No GPU path (remote session, driver trouble): the easter egg simply does not play.
            RuApp.Services.GuiDiagnosticWriter.AppendTraceLine($"[BlackHole] Skipped: {error.GetType().Name} 0x{error.HResult:X8}");
        }
        finally
        {
            renderer?.Dispose();
            Children.Clear();
            Background = null;
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;
            IsPlaying = false;
        }
    }

    /// <summary>Renders on every composition frame until the timeline ends.</summary>
    private Task RunAsync(BlackHoleRenderer renderer)
    {
        var done = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        float phase = 0, last = 0;
        renderer.Render(Frame(renderer, 0, 0));

        void OnFrame(object? sender, object e)
        {
            var now = (float)clock.Elapsed.TotalSeconds;
            if (now >= Total)
            {
                done.TrySetResult();
                return;
            }

            var feeding = Smooth(0.3f, CriticalAt, now) + 2.5f * Smooth(CriticalAt, ImplodeAt, now);
            phase += (now - last) * (0.9f + 2.2f * feeding);
            last = now;
            try
            {
                renderer.Render(Frame(renderer, now, phase));
            }
            catch (Exception error)
            {
                done.TrySetException(error);
            }
        }

        var frames = 0;
        CompositionTarget.Rendering += OnFrame;
        CompositionTarget.Rendering += Count;
        return Finish();

        void Count(object? sender, object e) => frames++;

        async Task Finish()
        {
            try
            {
                await done.Task;
            }
            finally
            {
                CompositionTarget.Rendering -= OnFrame;
                CompositionTarget.Rendering -= Count;
                RuApp.Services.GuiDiagnosticWriter.AppendTraceLine(
                    $"[BlackHole] {frames / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0} fps at {renderer.Width}x{renderer.Height}");
            }
        }
    }

    /// <summary>The whole effect as a function of time: camera, disk, suction, blast.</summary>
    private BlackHoleRenderer.Scene Frame(BlackHoleRenderer renderer, float t, float phase)
    {
        var resolution = new Vector2(renderer.Width, renderer.Height);
        var cameraDistance = t switch
        {
            < FormedAt => LogLerp(StartDistance, ApproachDistance, EaseOut(t / FormedAt)),
            < CriticalAt => LogLerp(ApproachDistance, ClosestDistance, EaseInOut((t - FormedAt) / (CriticalAt - FormedAt))),
            // Unstable: it throbs, faster and harder, before it gives way.
            < ImplodeAt => ClosestDistance * (1 + 0.09f * MathF.Sin((t - CriticalAt) * (30 + 40 * (t - CriticalAt)))),
            < ExplodeAt => LogLerp(ClosestDistance, 9000, MathF.Pow((t - ImplodeAt) / (ExplodeAt - ImplodeAt), 2.2f)),
            _ => 1e6f,
        };

        var maxShock = resolution.Length() * 0.5f + 260;
        var shock = t >= ExplodeAt ? maxShock * EaseOut(Math.Min(1, (t - ExplodeAt) / ShockSeconds)) : -1;
        var flash = t < ExplodeAt - 0.04f ? 0
            : t < ExplodeAt + 0.04f ? 0.9f * (t - ExplodeAt + 0.04f) / 0.08f
            : 0.9f * MathF.Exp(-(t - ExplodeAt - 0.04f) * 4.6f);

        var shake = t switch
        {
            < FormedAt => 0.6f * t,
            < CriticalAt => 0.6f + 2.4f * (t - FormedAt) / (CriticalAt - FormedAt),
            < ExplodeAt => 3f + 9f * (t - CriticalAt) / (ExplodeAt - CriticalAt),
            _ => 22f * MathF.Exp(-(t - ExplodeAt) * 3.5f),
        } * (renderer.Height / 900f);

        var shadowAngle = 5.196f / MathF.Max(cameraDistance, 6);
        return new BlackHoleRenderer.Scene
        {
            Resolution = resolution,
            Center = resolution / 2,
            Time = t,
            TanHalf = TanHalf,
            CameraDistance = cameraDistance,
            HoleOn = t < ExplodeAt ? 1 : 0,
            Disk = t < ExplodeAt ? Smooth(0.35f, 3.4f, t) : 0,
            Heat = t < ExplodeAt ? Smooth(CriticalAt - 0.3f, ImplodeAt + 0.1f, t) : 0,
            Pull = MathF.Pow(Smooth(0.9f, CriticalAt, t), 1.3f),
            Swirl = 3.4f * MathF.Pow(Smooth(0.6f, CriticalAt, t), 1.5f),
            Phase = phase,
            Flash = Math.Clamp(flash, 0, 1),
            Shock = shock,
            MaxShock = maxShock,
            ShadowPx = MathF.Tan(shadowAngle) / TanHalf * renderer.Height * 0.5f,
            // Smooth handheld shake: a few incommensurate sines, not per-frame noise.
            ShakeOffset = new Vector2(
                MathF.Sin(t * 37.1f) + 0.6f * MathF.Sin(t * 23.3f + 1.7f) + 0.3f * MathF.Sin(t * 61.7f + 0.4f),
                MathF.Sin(t * 31.7f + 0.6f) + 0.6f * MathF.Sin(t * 19.1f + 2.3f) + 0.3f * MathF.Sin(t * 57.3f + 1.1f)) / 1.9f * shake,
            Fade = Smooth(Total - 0.45f, Total - 0.05f, t),
        };
    }

    private static float LogLerp(float from, float to, float t) => MathF.Exp(MathF.Log(from) + (MathF.Log(to) - MathF.Log(from)) * t);

    private static float EaseOut(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static float EaseInOut(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
