using Polly.Telemetry;

namespace Polly.Builder;

/// <summary>
/// A builder that is used to create an instance of <see cref="ResilienceStrategy"/>.
/// </summary>
/// <remarks>
/// The builder supports chaining multiple strategies into a pipeline of strategies.
/// The resulting instance of <see cref="ResilienceStrategy"/> created by the <see cref="Build"/> call will execute the strategies in the same order they were added to the builder.
/// The order of the strategies is important.
/// </remarks>
public class ResilienceStrategyBuilder
{
    private readonly List<Entry> _entries = new();
    private ResilienceStrategyBuilderOptions _options = new();
    private bool _used;

    /// <summary>
    /// Gets or sets the builder options.
    /// </summary>
    public ResilienceStrategyBuilderOptions Options
    {
        get => _options;
        set => _options = Guard.NotNull(value);
    }

    /// <summary>
    /// Adds an already created strategy instance to the builder.
    /// </summary>
    /// <param name="strategy">The strategy instance.</param>
    /// <param name="options">The options associated with the strategy. If none are provided the default instance of <see cref="ResilienceStrategyOptions"/> is created.</param>
    /// <returns>The same builder instance.</returns>
    public ResilienceStrategyBuilder AddStrategy(ResilienceStrategy strategy, ResilienceStrategyOptions? options = null)
    {
        Guard.NotNull(strategy);

        return AddStrategy(_ => strategy, options);
    }

    /// <summary>
    /// Adds a strategy to the builder.
    /// </summary>
    /// <param name="factory">The factory that creates a resilience strategy.</param>
    /// <param name="options">The options associated with the strategy. If none are provided the default instance of <see cref="ResilienceStrategyOptions"/> is created.</param>
    /// <returns>The same builder instance.</returns>
    public ResilienceStrategyBuilder AddStrategy(Func<ResilienceStrategyBuilderContext, ResilienceStrategy> factory, ResilienceStrategyOptions? options = null)
    {
        Guard.NotNull(factory);

        if (options is not null)
        {
            ValidationHelper.ValidateObject(options, $"The '{nameof(ResilienceStrategyOptions)}' options are not valid.");
        }

        if (_used)
        {
            throw new InvalidOperationException("Cannot add any more resilience strategies to the builder after it has been used to build a strategy once.");
        }

        _entries.Add(new Entry(factory, options ?? new ResilienceStrategyOptions()));

        return this;
    }

    /// <summary>
    /// Builds the resilience strategy.
    /// </summary>
    /// <returns>An instance of <see cref="ResilienceStrategy"/>.</returns>
    public ResilienceStrategy Build()
    {
        ValidationHelper.ValidateObject(Options, $"The '{nameof(ResilienceStrategyBuilderOptions)}' options are not valid.");

        _used = true;

        if (_entries.Count == 0)
        {
            return NullResilienceStrategy.Instance;
        }

        if (_entries.Count == 1)
        {
            return CreateResilienceStrategy(_entries[0]);
        }

        var strategies = _entries.Select(CreateResilienceStrategy).ToList();

        return ResilienceStrategyPipeline.CreatePipeline(strategies);
    }

    private ResilienceStrategy CreateResilienceStrategy(Entry entry)
    {
        var telemetryContext = new ResilienceTelemetryFactoryContext
        {
            BuilderName = Options.BuilderName,
            BuilderProperties = Options.Properties,
            StrategyName = entry.Properties.StrategyName,
            StrategyType = entry.Properties.StrategyType
        };

        var context = new ResilienceStrategyBuilderContext
        {
            BuilderName = Options.BuilderName,
            BuilderProperties = Options.Properties,
            StrategyName = entry.Properties.StrategyName,
            StrategyType = entry.Properties.StrategyType,
            Telemetry = Options.TelemetryFactory.Create(telemetryContext),
            TimeProvider = Options.TimeProvider
        };

        return entry.Factory(context);
    }

    private sealed record Entry(Func<ResilienceStrategyBuilderContext, ResilienceStrategy> Factory, ResilienceStrategyOptions Properties);
}
