using System.Drawing;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class FullscreenAppContextServiceTests
{
    [Fact]
    public void CoversBounds_AcceptsSmallRoundingDifferencesAroundMonitorEdges()
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        var window = new Rectangle(-1918, 2, 1916, 1076);

        Assert.True(FullscreenWindowClassifier.CoversBounds(window, monitor, tolerance: 2));
    }

    [Theory]
    [InlineData(-1917, 0, 1920, 1080)]
    [InlineData(-1920, 0, 1917, 1080)]
    [InlineData(-1920, 3, 1920, 1080)]
    [InlineData(-1920, 0, 1920, 1077)]
    public void CoversBounds_RejectsWindowsThatLeaveMoreThanToleranceUncovered(
        int x,
        int y,
        int width,
        int height)
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);

        Assert.False(FullscreenWindowClassifier.CoversBounds(
            new Rectangle(x, y, width, height), monitor, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_AcceptsBorderlessMonitorBoundsWithoutMaximizedState()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);

        Assert.True(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            monitor, monitor, workArea, isMaximized: false, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_AcceptsOsMaximizedWindowCoveringWorkArea()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var codexMaximizedBounds = new Rectangle(-8, -8, 1936, 1056);

        Assert.True(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            codexMaximizedBounds, monitor, workArea, isMaximized: true, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_RejectsLargeFloatingWindowThatIsNotMaximized()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var largeFloatingBounds = new Rectangle(-8, -8, 1936, 1056);

        Assert.False(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            largeFloatingBounds, monitor, workArea, isMaximized: false, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_RejectsMaximizedWindowThatDoesNotCoverWorkArea()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var restoredBounds = new Rectangle(40, 40, 1840, 960);

        Assert.False(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            restoredBounds, monitor, workArea, isMaximized: true, tolerance: 2));
    }

    [Theory]
    [InlineData(true, false, false, 10, 9, true, MonitorPlacementMode.Primary, true)]
    [InlineData(true, false, false, 11, 10, true, MonitorPlacementMode.Primary, true)]
    [InlineData(true, false, false, 11, 10, false, MonitorPlacementMode.Primary, false)]
    [InlineData(true, false, false, 11, 10, false, MonitorPlacementMode.ActiveWindow, true)]
    [InlineData(false, false, false, 11, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, true, false, 11, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, false, true, 11, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, false, false, 0, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, false, false, 10, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    public void IsEligibleTarget_AppliesForegroundWindowAndMonitorRules(
        bool isVisible,
        bool isMinimized,
        bool isDesktopShell,
        uint processId,
        uint ownProcessId,
        bool monitorIsPrimary,
        MonitorPlacementMode mode,
        bool expected)
    {
        Assert.Equal(expected, FullscreenWindowClassifier.IsEligibleTarget(
            isVisible,
            isMinimized,
            isDesktopShell,
            processId,
            ownProcessId,
            monitorIsPrimary,
            mode));
    }

    [Fact]
    public void SelectFirstAvailableIcon_PrefersWindowIconBeforeClassAndExecutableFallbacks()
    {
        var windowSmall2 = IntPtr.Zero;
        var windowSmall = new IntPtr(101);
        var windowBig = new IntPtr(102);
        var classSmall = new IntPtr(201);
        var executable = new IntPtr(301);
        var attempts = 0;

        Assert.Equal(windowSmall, FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => { attempts++; return windowSmall2; },
            () => { attempts++; return windowSmall; },
            () => { attempts++; return windowBig; },
            () => { attempts++; return classSmall; },
            () => { attempts++; return executable; }));
        Assert.Equal(2, attempts);

        Assert.Equal(classSmall, FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => IntPtr.Zero,
            () => IntPtr.Zero,
            () => classSmall,
            () => executable));
        Assert.Equal(IntPtr.Zero, FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => IntPtr.Zero,
            () => IntPtr.Zero));
    }

    [Fact]
    public void SelectPreferredIcon_PrefersExplicitWindowThenExecutableBeforeClassIcons()
    {
        var explicitWindowIcon = new object();
        var executableIcon = new object();
        var classIcon = new object();

        Assert.Same(explicitWindowIcon, FullscreenWindowClassifier.SelectPreferredIcon(
            [() => explicitWindowIcon],
            () => executableIcon,
            [() => classIcon],
            _ => true));

        Assert.Same(executableIcon, FullscreenWindowClassifier.SelectPreferredIcon(
            [() => null],
            () => executableIcon,
            [() => classIcon],
            _ => true));
    }

    [Fact]
    public void SelectPreferredIcon_SkipsGenericCandidatesAndRejectsThemWhenNoRealIconExists()
    {
        var genericWindowIcon = "generic-window";
        var genericExecutableIcon = "generic-executable";
        var genericClassIcon = "generic-class";
        var realClassIcon = "real-class";

        var selected = FullscreenWindowClassifier.SelectPreferredIcon(
            [() => genericWindowIcon],
            () => genericExecutableIcon,
            [() => genericClassIcon, () => realClassIcon],
            icon => !icon.StartsWith("generic", StringComparison.Ordinal));

        Assert.Equal(realClassIcon, selected);
        Assert.Null(FullscreenWindowClassifier.SelectPreferredIcon(
            [() => genericWindowIcon],
            () => genericExecutableIcon,
            [() => genericClassIcon],
            icon => !icon.StartsWith("generic", StringComparison.Ordinal)));
    }

    [Fact]
    public void IconRetryPolicy_UsesIncreasingDelaysThenStops()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), FullscreenIconRetryPolicy.GetDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(750), FullscreenIconRetryPolicy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), FullscreenIconRetryPolicy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), FullscreenIconRetryPolicy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), FullscreenIconRetryPolicy.GetDelay(4));
        Assert.Null(FullscreenIconRetryPolicy.GetDelay(5));
        Assert.Null(FullscreenIconRetryPolicy.GetDelay(-1));
    }

    [Fact]
    public void DiskIconCache_UsesContentAddressedBlobsAndStableIdentityKeys()
    {
        using var directory = new TemporaryDirectory();
        var cache = new FullscreenIconDiskCache(directory.Path);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD84AAAAASUVORK5CYII=");

        Assert.True(cache.TryWrite("app-path|length|mtime|dpi", png));
        Assert.True(cache.TryWrite("same-content-different-app", png));
        Assert.True(cache.TryRead("app-path|length|mtime|dpi", out var cached));
        Assert.Equal(png, cached);
        Assert.False(cache.TryRead("app-path|new-length|new-mtime|dpi", out _));
        Assert.Single(Directory.GetFiles(directory.Path, "*.png"));
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.ref").Length);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void DiskIconCache_RejectsInvalidPngAndCorruptContentGracefully()
    {
        using var directory = new TemporaryDirectory();
        var cache = new FullscreenIconDiskCache(directory.Path);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD84AAAAASUVORK5CYII=");

        Assert.False(cache.TryWrite("invalid", [1, 2, 3, 4]));
        Assert.Empty(Directory.GetFiles(directory.Path));

        Assert.True(cache.TryWrite("app", png));
        var contentPath = Assert.Single(Directory.GetFiles(directory.Path, "*.png"));
        File.WriteAllBytes(contentPath, [1, 2, 3, 4]);
        Assert.False(cache.TryRead("app", out _));
        Assert.True(cache.TryWrite("app", png));
        Assert.True(cache.TryRead("app", out var repaired));
        Assert.Equal(png, repaired);
    }

    [Fact]
    public void DiskIconCache_EnforcesEntryLimitAndRemovesOrphanedBlobs()
    {
        using var directory = new TemporaryDirectory();
        var cache = new FullscreenIconDiskCache(directory.Path, maximumEntries: 2);
        var firstPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD84AAAAASUVORK5CYII=");
        var secondPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

        Assert.True(cache.TryWrite("one", firstPng));
        Assert.True(cache.TryWrite("two", secondPng));
        Assert.True(cache.TryWrite("three", firstPng));

        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.ref").Length);
        Assert.InRange(Directory.GetFiles(directory.Path, "*.png").Length, 1, 2);
    }

    [Fact]
    public void DiskIconCache_ExecutableMetadataChangeInvalidatesStableKey()
    {
        using var directory = new TemporaryDirectory();
        var executablePath = System.IO.Path.Combine(directory.Path, "Codex.exe");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD84AAAAASUVORK5CYII=");
        File.WriteAllBytes(executablePath, [1, 2, 3]);
        var firstKey = FullscreenAppContextService.CreatePersistentIconCacheKey(executablePath, 144, "Chrome_WidgetWin_1");
        var cache = new FullscreenIconDiskCache(System.IO.Path.Combine(directory.Path, "cache"));

        Assert.NotNull(firstKey);
        Assert.True(cache.TryWrite(firstKey!, png));
        File.WriteAllBytes(executablePath, [1, 2, 3, 4]);
        var updatedKey = FullscreenAppContextService.CreatePersistentIconCacheKey(executablePath, 144, "Chrome_WidgetWin_1");

        Assert.NotEqual(firstKey, updatedKey);
        Assert.False(cache.TryRead(updatedKey!, out _));
    }

    [Fact]
    public void DiskIconCache_EnforcesTotalPngStorageBudget()
    {
        using var directory = new TemporaryDirectory();
        var firstPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD84AAAAASUVORK5CYII=");
        var secondPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        var budget = firstPng.Length + secondPng.Length - 1;
        var cache = new FullscreenIconDiskCache(directory.Path, maximumPngStorageBytes: budget);

        Assert.True(cache.TryWrite("one", firstPng));
        Assert.True(cache.TryWrite("two", secondPng));

        Assert.Single(Directory.GetFiles(directory.Path, "*.ref"));
        Assert.Single(Directory.GetFiles(directory.Path, "*.png"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NotchBar.IconCache.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
