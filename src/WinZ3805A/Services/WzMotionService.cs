using System.Runtime.Versioning;

using Microsoft.UI.Dispatching;

using Windows.UI.ViewManagement;

namespace WinZ3805A.Services;

/// <summary>
/// Whether the user has asked Windows for animations, per §9.8's reduced-motion rule.
/// </summary>
/// <remarks>
/// An interface so that the one implementation may hold a WinRT settings object and the tests may
/// hold a bool. Nothing here is about a particular animation — callers ask
/// <see cref="MotionPolicy"/> what to do with the answer.
/// </remarks>
public interface IMotionService
{
    /// <summary>Whether animations are enabled system-wide.</summary>
    /// <remarks>
    /// Settings &gt; Accessibility &gt; Visual effects &gt; Animation effects, which is also what a
    /// Windows power or performance profile turns off on the user's behalf.
    /// </remarks>
    bool AnimationsEnabled { get; }

    /// <summary>Raised on the UI thread after <see cref="AnimationsEnabled"/> changes.</summary>
    event EventHandler? AnimationsEnabledChanged;
}

/// <summary>
/// §9.8's <c>WzMotionService</c>: <see cref="UISettings.AnimationsEnabled"/>, read once and then
/// watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is §9.8's and §6.2's, not this codebase's convention.</b> Nothing else in
/// <c>WinZ3805A.Services</c> carries the <c>Wz</c> prefix — §6.2 gives it to design tokens — but
/// both §9.8's reduced-motion paragraph and §6.2's source tree name this service in full, so it is
/// spelt the way the specification spells it. The interface above is not named in either place and
/// follows the convention the rest of the folder uses.
/// </para>
/// <para>
/// §9.8 asks for the setting to be read <i>at startup and subscribed to</i>, and the second half is
/// the half that costs something: a user who turns animations off while the Details window is open
/// has done so because something on screen is making them ill, and a value latched at launch would
/// keep moving until they restarted the application.
/// </para>
/// <para>
/// <see cref="UISettings"/> is held in a field rather than constructed per read. Its events are
/// raised by the system against that instance, and an instance nothing references is collected —
/// which does not fail loudly, it simply stops delivering the notification this class exists for.
/// </para>
/// <para>
/// <b>The second half is unavailable below Windows 10 2004.</b> <c>UISettings.AnimationsEnabled</c>
/// is readable from 1809, which is §6.1's floor, but
/// <c>UISettings.AnimationsEnabledChanged</c> arrived in 19041. On an older build the setting is
/// therefore read once and honoured for the life of the process, and a user who changes it while
/// the application is running sees the new value at the next launch. That is the whole of the
/// degradation, and it is not worth raising §6.1's minimum over: the value is never <i>wrong</i>,
/// only late.
/// </para>
/// </remarks>
public sealed class WzMotionService : IMotionService, IDisposable
{
    private readonly UISettings _settings = new();
    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();

    private bool _animationsEnabled;
    private bool _disposed;

    /// <summary>Reads the current setting and, where the build supports it, begins watching it.</summary>
    public WzMotionService()
    {
        _animationsEnabled = _settings.AnimationsEnabled;

        if (CanWatch)
        {
            _settings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        }
    }

    /// <inheritdoc />
    public event EventHandler? AnimationsEnabledChanged;

    /// <inheritdoc />
    public bool AnimationsEnabled => _animationsEnabled;

    /// <summary>Whether this Windows build raises <c>UISettings.AnimationsEnabledChanged</c>.</summary>
    /// <remarks>
    /// Written as a version check rather than an <c>ApiInformation</c> probe because the platform
    /// compatibility analyzer understands this one, so a call added below the floor is a build
    /// error rather than something the next person finds on a 1809 machine. The attribute is what
    /// carries that knowledge across the property boundary — without it the analyzer sees only a
    /// bool and reports the guarded call anyway.
    /// </remarks>
    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    private static bool CanWatch => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Stops watching the setting.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (CanWatch)
        {
            _settings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        }
    }

    /// <remarks>
    /// Raised on a system thread, so the new value is read here and delivered on the UI thread —
    /// every subscriber is a window choosing a transition, and a transition assigned off-thread is
    /// a marshalling exception at best.
    /// </remarks>
    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        bool enabled = sender.AnimationsEnabled;

        void Publish()
        {
            if (_disposed || enabled == _animationsEnabled)
            {
                return;
            }

            _animationsEnabled = enabled;
            AnimationsEnabledChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_ui is null || _ui.HasThreadAccess)
        {
            Publish();
        }
        else
        {
            _ui.TryEnqueue(Publish);
        }
    }
}
