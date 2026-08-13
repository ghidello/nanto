using AwesomeAssertions;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowsAppearanceManagerTests
{
    public static TheoryData<int> AppearanceAcquisitionCheckpoints => new()
    {
        (int)Phase1AcquisitionCheckpoint.SystemAppearanceSourceCreated,
        (int)Phase1AcquisitionCheckpoint.SystemAppearanceSubscriptionAdded,
    };

    [Theory]
    [MemberData(nameof(AppearanceAcquisitionCheckpoints))]
    public async Task AppearanceAcquisitionFailureReleasesEveryResource(int failingCheckpointValue)
    {
        var failingCheckpoint = (Phase1AcquisitionCheckpoint)failingCheckpointValue;
        var failure = new InvalidOperationException($"{failingCheckpoint} failed");
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            var create = () => dispatcher.InvokeAsync(
                () => WindowsAppearanceManager.Create(
                    dispatcher,
                    ledger,
                    new DelegatePhase1FailureInjector(checkpoint =>
                    {
                        if (checkpoint == failingCheckpoint)
                        {
                            throw failure;
                        }
                    }),
                    TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken).AsTask();

            (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task ProjectedSystemAppearanceCanBeReadAndReleasedOnTheUiThread()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var source = new WindowsSystemAppearanceSource(ledger, NoOpPhase1FailureInjector.Instance);
                    _ = source.IsDark;
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task ExplicitPreferencesUpdateTheNativeWindowAttribute()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    using var window = new Win32Window(
                        windowClass,
                        ledger,
                        dispatcher,
                        "Nanto appearance test window",
                        640,
                        480,
                        WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                        WINDOW_STYLE.WS_OVERLAPPEDWINDOW);
                    using var manager = new WindowsAppearanceManager(dispatcher, new FakeSystemAppearanceSource());
                    using var attachment = manager.AttachWindow(window.Handle, ColorSchemePreference.Light);

                    ReadDarkMode(window.Handle).Should().BeFalse();
                    manager.SetPreferredColorScheme(ColorSchemePreference.Dark);
                    ReadDarkMode(window.Handle).Should().BeTrue();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Theory]
    [InlineData(ColorSchemePreference.System, false, false)]
    [InlineData(ColorSchemePreference.System, true, true)]
    [InlineData(ColorSchemePreference.Light, true, false)]
    [InlineData(ColorSchemePreference.Dark, false, true)]
    public void PreferenceResolutionUsesTheSystemOnlyForSystemMode(
        ColorSchemePreference preference,
        bool systemIsDark,
        bool expectedDarkMode)
    {
        WindowsAppearanceManager.ResolveDarkMode(preference, systemIsDark).Should().Be(expectedDarkMode);
    }

    [Fact]
    public void SystemChangesUpdateOnlyAnAttachedSystemModeWindow()
    {
        var dispatcher = new InlineDispatcher();
        var source = new FakeSystemAppearanceSource();
        var applications = new List<bool>();
        using var manager = new WindowsAppearanceManager(
            dispatcher,
            source,
            (_, useDarkMode, _) => applications.Add(useDarkMode));
        using var attachment = manager.AttachWindow(new HWND((nint)1), ColorSchemePreference.System);

        source.IsDark = true;
        source.RaiseChanged();
        manager.SetPreferredColorScheme(ColorSchemePreference.Light);
        source.IsDark = false;
        source.RaiseChanged();

        applications.Should().Equal(false, true, false);
        manager.PreferredColorScheme.Should().Be(ColorSchemePreference.Light);
    }

    [Theory]
    [InlineData(ColorSchemePreference.Light, false)]
    [InlineData(ColorSchemePreference.Dark, true)]
    public void ExplicitPreferencesDoNotReadSystemAppearance(ColorSchemePreference preference, bool expectedDarkMode)
    {
        var dispatcher = new InlineDispatcher();
        var source = new FakeSystemAppearanceSource { ThrowWhenRead = true };
        bool? appliedDarkMode = null;
        using var manager = new WindowsAppearanceManager(
            dispatcher,
            source,
            (_, useDarkMode, _) => appliedDarkMode = useDarkMode);
        using var attachment = manager.AttachWindow(new HWND((nint)1), preference);

        appliedDarkMode.Should().Be(expectedDarkMode);
    }

    [Fact]
    public void DetachedWindowsIgnoreLaterSystemChanges()
    {
        var dispatcher = new InlineDispatcher();
        var source = new FakeSystemAppearanceSource();
        var applicationCount = 0;
        using var manager = new WindowsAppearanceManager(
            dispatcher,
            source,
            (_, _, _) => applicationCount++);
        var attachment = manager.AttachWindow(new HWND((nint)1), ColorSchemePreference.System);

        attachment.Dispose();
        source.IsDark = true;
        source.RaiseChanged();

        applicationCount.Should().Be(1);
    }

    private static bool ReadDarkMode(HWND window)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        var result = PInvoke.DwmGetWindowAttribute(
            window,
            DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
            value);
        result.Succeeded.Should().BeTrue();
        return BitConverter.ToInt32(value) != 0;
    }

    private sealed class FakeSystemAppearanceSource : IWindowsSystemAppearanceSource
    {
        private bool _isDark;

        public bool IsDark
        {
            get => ThrowWhenRead
                ? throw new InvalidOperationException("System appearance must not be read.")
                : _isDark;
            set => _isDark = value;
        }

        public bool ThrowWhenRead { get; init; }

        public event Action? Changed;

        public void Dispose()
        {
        }

        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }

        public ValueTask<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(action());
        }

        public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default) =>
            action(cancellationToken);

        public ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default) =>
            action(cancellationToken);
    }
}