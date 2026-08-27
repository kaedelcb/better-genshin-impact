using System.Xml.Linq;
using Xunit;

namespace BetterGenshinImpact.UnitTest.CoreTests;

/// <summary>
/// Bug Condition Exploration Test — System.Drawing.Pen Ambiguous Reference
///
/// **Validates: Requirements 1.1, 1.2, 1.3, 1.4**
///
/// This test verifies that the dual-reference bug condition exists in the csproj file:
///   C(X) := csproj contains both:
///     (a) A <![CDATA[<PackageReference Include="System.Drawing.Common" />]]> element
///     (b) A <![CDATA[<UseWindowsForms>true</UseWindowsForms>]]> property
///
/// On UNFIXED code: the test PASSES (confirms the bug condition is present).
/// On FIXED code:   the test FAILS (the package reference is removed, dual reference no longer exists).
/// </summary>
public class SystemDrawingPenAmbiguityBugConditionTest
{
    private static readonly string CsprojPath = Path.GetFullPath(
        Path.Combine("..", "..", "..", "..", "..", "BetterGenshinImpact", "BetterGenshinImpact.csproj"));

    private static XDocument LoadCsproj()
    {
        Assert.True(File.Exists(CsprojPath), $"csproj file not found at: {CsprojPath}");
        return XDocument.Load(CsprojPath);
    }

    /// <summary>
    /// Bug Condition (a): The csproj contains a <![CDATA[<PackageReference Include="System.Drawing.Common" />]]> element.
    ///
    /// On unfixed code, this assertion PASSES (the package reference exists, confirming the dual-reference condition).
    /// After fix, this assertion FAILS (the package reference is removed).
    ///
    /// **Validates: Requirements 1.1, 1.2**
    /// </summary>
    [Fact]
    public void BugCondition_PackageReference_SystemDrawingCommon_Exists()
    {
        // Arrange
        var doc = LoadCsproj();

        // Act
        var packageRef = doc.Descendants("PackageReference")
            .FirstOrDefault(e => string.Equals(
                e.Attribute("Include")?.Value,
                "System.Drawing.Common",
                StringComparison.OrdinalIgnoreCase));

        // Assert
        Assert.NotNull(packageRef);
        Assert.Equal("System.Drawing.Common", packageRef.Attribute("Include")?.Value);
    }

    /// <summary>
    /// Bug Condition (b): The csproj contains <![CDATA[<UseWindowsForms>true</UseWindowsForms>]]> property.
    ///
    /// This property is part of the bug condition because it causes the framework
    /// to provide System.Drawing.dll, creating the dual-reference ambiguity with
    /// the System.Drawing.Common NuGet package.
    ///
    /// On unfixed code, this assertion PASSES.
    /// After fix, this assertion still PASSES (the property is preserved).
    ///
    /// **Validates: Requirements 1.3, 1.4**
    /// </summary>
    [Fact]
    public void BugCondition_UseWindowsForms_IsTrue()
    {
        // Arrange
        var doc = LoadCsproj();

        // Act
        var useWindowsForms = doc.Descendants("UseWindowsForms")
            .FirstOrDefault();

        // Assert
        Assert.NotNull(useWindowsForms);
        Assert.Equal("true", useWindowsForms.Value, ignoreCase: true);
    }

    /// <summary>
    /// Combined Bug Condition C(X): Both conditions are simultaneously true.
    ///
    /// This is the definitive test for the bug condition:
    ///   isBugCondition(csproj) :=
    ///     HasPackageReference(csproj, "System.Drawing.Common")
    ///     AND HasProperty(csproj, "UseWindowsForms", "true")
    ///
    /// On unfixed code: PASSES (confirms the dual reference exists — the bug is present).
    /// After fix:       FAILS (the package reference is removed, breaking the condition).
    ///
    /// **Validates: Requirements 1.1, 1.2, 1.3, 1.4**
    /// </summary>
    [Fact]
    public void BugCondition_Combined_DualReferenceExists()
    {
        // Arrange
        var doc = LoadCsproj();

        // Act - Condition (a): PackageReference exists
        var hasSystemDrawingCommon = doc.Descendants("PackageReference")
            .Any(e => string.Equals(
                e.Attribute("Include")?.Value,
                "System.Drawing.Common",
                StringComparison.OrdinalIgnoreCase));

        // Act - Condition (b): UseWindowsForms=true exists
        var hasUseWindowsFormsTrue = doc.Descendants("UseWindowsForms")
            .Any(e => string.Equals(e.Value, "true", StringComparison.OrdinalIgnoreCase));

        // Assert: Both conditions must be true simultaneously for the bug condition C(X)
        Assert.True(hasSystemDrawingCommon,
            "Bug Condition (a) FAILED: System.Drawing.Common package reference not found in csproj. " +
            "Expected the package reference to exist (unfixed code).");
        Assert.True(hasUseWindowsFormsTrue,
            "Bug Condition (b) FAILED: UseWindowsForms=true not found in csproj. " +
            "Expected the property to exist.");

        // Confirm both conditions are true = bug condition C(X) is present
        Assert.True(hasSystemDrawingCommon && hasUseWindowsFormsTrue,
            $"Bug Condition C(X) FAILED: hasSystemDrawingCommon={hasSystemDrawingCommon}, " +
            $"hasUseWindowsFormsTrue={hasUseWindowsFormsTrue}. " +
            "Expected both to be true (dual reference exists on unfixed code).");
    }
}