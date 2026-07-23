using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExecutionModeLoader
{
    internal static T Load<T>(string implementationName, string subject)
    {
        Type? implementation = typeof(ActualDebugUnitTestExecutionModeLoader)
            .Assembly.GetType(implementationName, throwOnError: false);
        if (implementation is null)
            throw new InvalidOperationException(
                $"{subject} implementation is missing");
        if (!typeof(T).IsAssignableFrom(implementation))
            throw new InvalidOperationException(
                $"{subject} does not implement its frozen contract");
        return (T)Activator.CreateInstance(
            implementation, BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic, binder: null, args: null,
            culture: null)!;
    }
}

internal static class ActualDebugUnitTestPreflightLoader
{
    internal static IActualDebugUnitTestPreflight Load() =>
        ActualDebugUnitTestExecutionModeLoader
            .Load<IActualDebugUnitTestPreflight>(
                "ONI_Together.HeadlessTests.ActualDebugUnitTestPreflight",
                "ActualDebugUnitTestPreflight");
}

internal static class ActualDebugUnitTestInstrumentationCacheLoader
{
    internal static IActualDebugUnitTestInstrumentationCache Load() =>
        ActualDebugUnitTestExecutionModeLoader
            .Load<IActualDebugUnitTestInstrumentationCache>(
                "ONI_Together.HeadlessTests." +
                "ActualDebugUnitTestInstrumentationCache",
                "ActualDebugUnitTestInstrumentationCache");
}

internal static class ActualDebugUnitTestBatchOnceLoader
{
    internal static IActualDebugUnitTestBatchOnce Load() =>
        ActualDebugUnitTestExecutionModeLoader
            .Load<IActualDebugUnitTestBatchOnce>(
                "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchOnce",
                "ActualDebugUnitTestBatchOnce");
}

internal static class ActualDebugUnitTestBatchMilestoneLoader
{
    internal static IActualDebugUnitTestBatchMilestone Load() =>
        ActualDebugUnitTestExecutionModeLoader
            .Load<IActualDebugUnitTestBatchMilestone>(
                "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchMilestone",
                "ActualDebugUnitTestBatchMilestone");
}
