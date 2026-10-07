using System.Reflection;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// How this project's tests run a workspace: on one thread with a message loop, as the window's dispatcher runs it
/// (<see cref="SingleThreadedContext"/>). A test that awaited on the pool instead resumed beside the workspace's own
/// continuations, and read a list or a navigation they were changing, which failed now and then in the full suites.
/// </summary>
public sealed class TestDisciplineTests
{
    [Fact(DisplayName = "§13.6: every workspace test runs on the single-threaded context the window's dispatcher stands for, never awaiting on the pool beside the workspace")]
    public void EveryTestRunsOnTheSingleThreadedContext()
    {
        string[] pooled =
        [
            .. typeof(TestDisciplineTests).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any()
                    && typeof(Task).IsAssignableFrom(method.ReturnType))
                .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
                .Order(StringComparer.Ordinal),
        ];
        Assert.True(pooled.Length == 0, "Run these through SingleThreadedContext.Run: " + string.Join(", ", pooled));
    }
}
