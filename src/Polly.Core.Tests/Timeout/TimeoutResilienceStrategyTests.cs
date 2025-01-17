using Moq;
using Polly.Telemetry;
using Polly.Timeout;

namespace Polly.Core.Tests.Timeout;

public class TimeoutResilienceStrategyTests : IDisposable
{
    private readonly Mock<ResilienceTelemetry> _telemetry;
    private readonly FakeTimeProvider _timeProvider;
    private readonly TimeoutStrategyOptions _options;
    private readonly CancellationTokenSource _cancellationSource;
    private readonly TimeSpan _delay = TimeSpan.FromSeconds(12);
    private bool _telemetryCalled;

    public TimeoutResilienceStrategyTests()
    {
        _telemetry = new Mock<ResilienceTelemetry>(MockBehavior.Strict);
        _timeProvider = new FakeTimeProvider();
        _options = new TimeoutStrategyOptions();
        _cancellationSource = new CancellationTokenSource();

        _telemetry.Setup(v => v.Report(TimeoutConstants.OnTimeoutEvent, It.IsAny<ResilienceContext>())).Callback<string, ResilienceContext>((_, _) =>
        {
            _telemetryCalled = true;
        });
    }

    public static TheoryData<TimeSpan> Execute_NoTimeout_Data() => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(-1),
        TimeoutStrategyOptions.InfiniteTimeout
    };

    public void Dispose() => _cancellationSource.Dispose();

    [Fact]
    public void Execute_EnsureTimeoutGeneratorCalled()
    {
        var called = false;
        _options.TimeoutGenerator.SetTimeout(args =>
        {
            args.Context.Should().NotBeNull();
            called = true;
            return TimeSpan.Zero;
        });

        var sut = CreateSut();

        sut.Execute(_ => { });

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_EnsureOnTimeoutCalled()
    {
        var called = false;
        _options.TimeoutGenerator.SetTimeout(args => _delay);
        _options.OnTimeout.Add(args =>
        {
            args.Exception.Should().BeAssignableTo<OperationCanceledException>();
            args.Timeout.Should().Be(_delay);
            args.Context.Should().NotBeNull();
            args.Context.CancellationToken.IsCancellationRequested.Should().BeTrue();
            called = true;
        });

        _timeProvider.SetupCancelAfterNow(_delay);

        var sut = CreateSut();
        await sut.Invoking(s => sut.ExecuteTaskAsync(token => Task.Delay(_delay, token))).Should().ThrowAsync<TimeoutRejectedException>();

        called.Should().BeTrue();
    }

    [MemberData(nameof(Execute_NoTimeout_Data))]
    [Theory]
    public void Execute_NoTimeout(TimeSpan timeout)
    {
        var called = false;
        var sut = CreateSut();
        _options.TimeoutGenerator.SetTimeout(args =>
        {
            called = true;
            return timeout;
        });
        sut.Execute(_ => { });

        called.Should().BeFalse();
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task Execute_Timeout(bool defaultCancellationToken)
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = defaultCancellationToken ? default : cts.Token;
        _options.TimeoutGenerator.SetTimeout(args => TimeSpan.FromSeconds(2));
        _timeProvider.SetupCancelAfterNow(TimeSpan.FromSeconds(2));
        var sut = CreateSut();

        await sut
            .Invoking(s => s.ExecuteTaskAsync(token => Delay(token), token))
            .Should().ThrowAsync<TimeoutRejectedException>()
            .WithMessage("The operation didn't complete within the allowed timeout of '00:00:02'.");

        _timeProvider.VerifyAll();
    }

    [Fact]
    public async Task Execute_Cancelled_EnsureNoTimeout()
    {
        var delay = TimeSpan.FromSeconds(10);

        var onTimeoutCalled = false;
        using var cts = new CancellationTokenSource();
        _options.TimeoutGenerator.SetTimeout(args => TimeSpan.FromSeconds(10));
        _options.OnTimeout.Add(() => onTimeoutCalled = true);
        _timeProvider.Setup(v => v.CancelAfter(It.IsAny<CancellationTokenSource>(), delay));

        var sut = CreateSut();

        await sut.Invoking(s => s.ExecuteTaskAsync(token => Delay(token, () => cts.Cancel()), cts.Token))
                 .Should()
                 .ThrowAsync<OperationCanceledException>();

        _timeProvider.VerifyAll();
        _telemetryCalled.Should().BeFalse();
        onTimeoutCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Execute_NoTimeoutOrCancellation_EnsureCancellationTokenRestored()
    {
        var delay = TimeSpan.FromSeconds(10);

        using var cts = new CancellationTokenSource();
        _options.TimeoutGenerator.SetTimeout(args => TimeSpan.FromSeconds(10));
        _timeProvider.Setup(v => v.CancelAfter(It.IsAny<CancellationTokenSource>(), delay));

        var sut = CreateSut();

        var context = ResilienceContext.Get();
        context.CancellationToken = cts.Token;

        await sut.ExecuteTaskAsync(
            (r, _) =>
            {
                r.CancellationToken.Should().NotBe(cts.Token);
                return Task.CompletedTask;
            },
            context,
            string.Empty);

        context.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Execute_EnsureCancellationTokenRegistrationNotExecutedOnSynchronizationContext()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        _options.TimeoutGenerator.SetTimeout(args => TimeSpan.FromSeconds(10));
        _timeProvider.Setup(v => v.CancelAfter(It.IsAny<CancellationTokenSource>(), TimeSpan.FromSeconds(10)));

        var sut = CreateSut();

        var mockSynchronizationContext = new Mock<SynchronizationContext>(MockBehavior.Strict);
        mockSynchronizationContext
            .Setup(x => x.Post(It.IsAny<SendOrPostCallback>(), It.IsAny<object>()))
            .Callback<SendOrPostCallback, object>((callback, state) => callback(state));

        mockSynchronizationContext
            .Setup(x => x.CreateCopy())
            .Returns(mockSynchronizationContext.Object);

        SynchronizationContext.SetSynchronizationContext(mockSynchronizationContext.Object);

        // Act
        try
        {
            await sut.ExecuteTaskAsync(token => Delay(token, () => cts.Cancel()), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // ok
        }

        // Assert
        mockSynchronizationContext.Verify(x => x.Post(It.IsAny<SendOrPostCallback>(), It.IsAny<object>()), Times.Never());
    }

    private TimeoutResilienceStrategy CreateSut() => new(_options, _timeProvider.Object, _telemetry.Object);

    private static Task Delay(CancellationToken token, Action? onWaiting = null)
    {
        Task delayTask = Task.CompletedTask;

        try
        {
            return Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        finally
        {
            onWaiting?.Invoke();
        }
    }
}
