using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class RootlessBuildExecutorTests
{
    [TestMethod]
    public void MappedRootArguments_Leaves_Mount_Propagation_Unchanged()
    {
        var arguments = RootlessBuildExecutor.MappedRootArguments("id", ["-u"]);

        CollectionAssert.AreEqual(
            new[]
            {
                "--fork",
                "--pid",
                "--mount",
                "--propagation",
                "unchanged",
                "--map-auto",
                "--map-root-user",
                "--setuid",
                "0",
                "--setgid",
                "0",
                "id",
                "-u"
            },
            arguments.ToArray());
    }
}
