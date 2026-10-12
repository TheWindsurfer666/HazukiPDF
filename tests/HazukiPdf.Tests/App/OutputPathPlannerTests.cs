using HazukiPdf.WinForms.Services;

namespace HazukiPdf.Tests.App;

public class OutputPathPlannerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "hazuki-planner");

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void AddsSuffixNextToOriginal()
    {
        var planner = new OutputPathPlanner([P("a", "report.pdf")], null, "_compressed");
        Assert.Equal(P("a", "report_compressed.pdf"), planner.Plan(P("a", "report.pdf")));
    }

    [Fact]
    public void UsesOutputFolderAndAllowsEmptySuffixThere()
    {
        var planner = new OutputPathPlanner([P("a", "report.pdf")], P("out"), "");
        Assert.Equal(P("out", "report.pdf"), planner.Plan(P("a", "report.pdf")));
    }

    [Fact]
    public void NeverReturnsAnInputPath()
    {
        // An empty suffix next to the original would overwrite it, so the default suffix is used.
        var planner = new OutputPathPlanner([P("a", "report.pdf")], null, "");
        Assert.Equal(P("a", "report_compressed.pdf"), planner.Plan(P("a", "report.pdf")));

        // The suffixed name is itself one of the inputs.
        string[] inputs = [P("a", "x.pdf"), P("a", "x_compressed.pdf")];
        planner = new OutputPathPlanner(inputs, null, "_compressed");
        Assert.Equal(P("a", "x_compressed (2).pdf"), planner.Plan(inputs[0]));
        Assert.Equal(P("a", "x_compressed_compressed.pdf"), planner.Plan(inputs[1]));
    }

    [Fact]
    public void SameNameFromTwoFoldersGetsDistinctOutputs()
    {
        string[] inputs = [P("a", "scan.pdf"), P("b", "scan.pdf"), P("c", "scan.pdf")];
        var planner = new OutputPathPlanner(inputs, P("out"), "_small");
        Assert.Equal(
            [P("out", "scan_small.pdf"), P("out", "scan_small (2).pdf"), P("out", "scan_small (3).pdf")],
            inputs.Select(planner.Plan).ToArray());
    }

    [Fact]
    public void RejectsSuffixWithPathCharacters() =>
        Assert.Throws<ArgumentException>(() => new OutputPathPlanner([], null, "/../x"));
}
