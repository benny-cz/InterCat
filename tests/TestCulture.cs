using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace InterCat.Tests;

/// <summary>
/// Every test assembly runs in the invariant culture, whatever the machine's (plan §13.6). The suite is written and run
/// both in a container, whose culture is invariant, and on workstations in their own, where a test expecting "1,200" met
/// the "1 200" its culture writes; the product writes in the person's culture either way. A test of what the product writes
/// in a culture sets that culture itself.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "A test assembly is loaded only by its test host, which must read every test in one culture.")]
    internal static void RunInTheInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
