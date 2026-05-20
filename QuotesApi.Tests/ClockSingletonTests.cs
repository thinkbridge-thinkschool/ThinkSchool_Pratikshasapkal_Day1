using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Abstractions;
using QuotesApi.Services;
using QuotesApi.Tests.Fakes;

namespace QuotesApi.Tests;

public class ClockSingletonTests
{
    // Verifies the core singleton contract: same instance every resolution.
    [Fact]
    public void Singleton_SameInstance_WhenResolvedTwice()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IClock>();
        var second = provider.GetRequiredService<IClock>();

        Assert.Same(first, second);
    }

    // Singletons outlive scopes — the same instance must cross scope boundaries.
    [Fact]
    public void Singleton_SameInstance_AcrossChildScopes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var provider = services.BuildServiceProvider();

        IClock fromScope1, fromScope2;
        using (var scope = provider.CreateScope())
            fromScope1 = scope.ServiceProvider.GetRequiredService<IClock>();
        using (var scope = provider.CreateScope())
            fromScope2 = scope.ServiceProvider.GetRequiredService<IClock>();

        Assert.Same(fromScope1, fromScope2);
    }

    // Mutating the singleton via one reference must be visible through another,
    // proving shared state — the hallmark of singleton lifetime.
    [Fact]
    public void FakeClock_Singleton_SharedStatePropagates()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, FakeClock>();
        var provider = services.BuildServiceProvider();

        var pinned = new DateTimeOffset(2025, 6, 15, 9, 0, 0, TimeSpan.Zero);
        ((FakeClock)provider.GetRequiredService<IClock>()).UtcNow = pinned;

        var resolved = provider.GetRequiredService<IClock>();

        Assert.Equal(pinned, resolved.UtcNow);
    }

    // Contrast: transient lifetime creates a new instance on every resolution.
    [Fact]
    public void Transient_DifferentInstances_WhenResolvedTwice()
    {
        var services = new ServiceCollection();
        services.AddTransient<IClock, FakeClock>();
        var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IClock>();
        var second = provider.GetRequiredService<IClock>();

        Assert.NotSame(first, second);
    }
}
