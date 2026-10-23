using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Tests.Integration;

// Intercepts only selected Redis calls; everything else uses the isolated real Valkey fixture.
public class RedisCallProxy : DispatchProxy
{
    private object _target = null!;
    private Func<MethodInfo, object?[], Func<object?>, object?> _intercept = null!;

    public static T Wrap<T>(T target, Func<MethodInfo, object?[], Func<object?>, object?> intercept) where T : class
    {
        T proxy = Create<T, RedisCallProxy>();
        var state = (RedisCallProxy)(object)proxy;
        state._target = target;
        state._intercept = intercept;
        return proxy;
    }

    public static IConnectionMultiplexer WithDatabase(IConnectionMultiplexer connection, IDatabase db) =>
        Wrap(connection, (method, _, next) => method.Name == "GetDatabase" ? db : next());

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return _intercept(targetMethod, args ?? [], () =>
        {
            try { return targetMethod.Invoke(_target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        });
    }
}

internal sealed class MutableRecommendationClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
