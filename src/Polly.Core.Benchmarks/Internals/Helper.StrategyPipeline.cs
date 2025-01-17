using Polly;

namespace Polly.Core.Benchmarks;

internal static partial class Helper
{
    public static object CreatePipeline(PollyVersion technology, int count) => technology switch
    {
        PollyVersion.V7 => count == 1 ? Policy.NoOpAsync<int>() : Policy.WrapAsync(Enumerable.Repeat(0, count).Select(_ => Policy.NoOpAsync<int>()).ToArray()),

        PollyVersion.V8 => CreateStrategy(builder =>
        {
            for (var i = 0; i < count; i++)
            {
                builder.AddStrategy(new EmptyResilienceStrategy());
            }
        }),
        _ => throw new NotSupportedException()
    };
}
