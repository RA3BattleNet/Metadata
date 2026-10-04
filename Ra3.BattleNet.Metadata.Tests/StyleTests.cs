using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class StyleTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    [TestMethod]
    [DataRow("#123456", null, "#123456")]
    [DataRow("#FF000080", null, "#FF000080")]
    [DataRow("#FF000080", "CSS", "#FF000080")]
    [DataRow("#80FF0000", "ARGB", "#FF000080")]
    [DataRow("#00123456", "ARGB", "#12345600")]
    [DataRow("#FF123456", "ARGB", "#123456FF")]
    [DataRow("#a012Ab34", "ARGB", "#12Ab34a0")]
    public void ColorToCss_PreservesChannelsAndAlpha(string value, string? format, string expected)
    {
        MetadataColor.ToCss(value, format).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("#123456", "ARGB")]
    [DataRow("#123456", "RGBA")]
    [DataRow("#123456", "")]
    [DataRow("#123456", "css")]
    [DataRow("#12345G", null)]
    [DataRow("#123", null)]
    [DataRow("123456", null)]
    [DataRow("#123456789", null)]
    public void ColorToCss_RejectsInvalidDeclarations(string value, string? format)
    {
        var convert = () => MetadataColor.ToCss(value, format);
        convert.Should().Throw<FormatException>();
    }

    [TestMethod]
    public void Schemas_AllowOmittedFieldsWithoutMaterializingValues()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"style-optional-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var xml = Path.Combine(temp, "metadata.xml");
            File.WriteAllText(xml, """
                <Metadata SchemaVersion="1.0" ContentRevision="optional">
                  <Mod ID="Absent" />
                  <Mod ID="Empty"><Style /></Mod>
                  <Mod ID="Bags">
                    <Style>
                      <Logo />
                      <Controls>
                        <PrimaryLabel />
                        <SecondaryLabel />
                        <PrimaryButton><Hover /><Active /></PrimaryButton>
                        <SecondaryButton><Hover /><Active /></SecondaryButton>
                      </Controls>
                      <Background />
                    </Style>
                  </Mod>
                  <Mod ID="Partial">
                    <Style>
                      <Logo Width="400.5" Position="BottomLeft" OffsetX="0" OffsetY="-24.5" />
                      <Controls>
                        <SecondaryButton><Hover><FontWeight>550</FontWeight></Hover><BorderWidth>0</BorderWidth></SecondaryButton>
                        <PrimaryLabel><FontSize>14.5</FontSize><Color>#FF000080</Color></PrimaryLabel>
                      </Controls>
                      <Background><SecondaryColor Format="ARGB">#80191919</SecondaryColor></Background>
                    </Style>
                  </Mod>
                </Metadata>
                """);

            foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
                SchemaValidator.ValidateFile(xml, Path.Combine(RepoMetadataDir, name)).Should().BeEmpty();

            var loaded = Metadata.LoadFromFile(xml);
            loaded.Catalog().Mod("Absent")!.Raw.Find("Style").Should().BeNull();
            var empty = loaded.Catalog().Mod("Empty")!.Raw.Find("Style")!;
            empty.Children.Should().BeEmpty();
            var partial = loaded.Catalog().Mod("Partial")!.Raw;
            partial.Find("Style:Logo")!.Get("Height").Should().BeNull();
            partial.Find("Style:Logo")!.Get("OffsetX").Should().Be("0");
            partial.Find("Style:Controls:SecondaryButton:BorderWidth")!.Value.Should().Be("0");
            partial.Find("Style:Controls:PrimaryLabel:Color")!.Get("Format").Should().BeNull();
            var color = partial.Find("Style:Background:SecondaryColor")!;
            MetadataColor.ToCss(color.Value!, color.Get("Format")).Should().Be("#19191980");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("<Logo Width=\"0\" />")]
    [DataRow("<Logo Height=\"-1\" />")]
    [DataRow("<Logo Position=\"Left\" />")]
    [DataRow("<Logo OffsetX=\"NaN\" />")]
    [DataRow("<Controls><PrimaryLabel><FontSize>0</FontSize></PrimaryLabel></Controls>")]
    [DataRow("<Controls><PrimaryLabel><FontWeight>99</FontWeight></PrimaryLabel></Controls>")]
    [DataRow("<Controls><PrimaryLabel><FontWeight>901</FontWeight></PrimaryLabel></Controls>")]
    [DataRow("<Controls><PrimaryLabel><FontWeight>550.5</FontWeight></PrimaryLabel></Controls>")]
    [DataRow("<Controls><PrimaryButton><BorderWidth>-1</BorderWidth></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Color>garbage#112233suffix</Color></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Hover><Color Format=\"ARGB\">#123456</Color></Hover></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Active><BorderColor Format=\"RGBA\">#12345678</BorderColor></Active></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Active><BackgroundColor Format=\"\">#123456</BackgroundColor></Active></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Hover><Color>#12GG56</Color></Hover></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Hover><Color /></Hover></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryButton><Active><Hover /></Active></PrimaryButton></Controls>")]
    [DataRow("<Controls><PrimaryLabel /><PrimaryLabel /></Controls>")]
    [DataRow("<Controls><LaunchButton /></Controls>")]
    [DataRow("<Controls><PrimaryButton><BorderBrush>#000000</BorderBrush></PrimaryButton></Controls>")]
    [DataRow("<Background><Color Format=\"ARGB\">#123456</Color></Background>")]
    [DataRow("<Background><SecondaryColor>#12345</SecondaryColor></Background>")]
    [DataRow("<Background Random=\"yes\" />")]
    public void BothSchemas_RejectInvalidSuppliedStyleFields(string style)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"style-invalid-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, $"<Metadata SchemaVersion=\"1.0\" ContentRevision=\"invalid\"><Mod ID=\"Invalid\"><Style>{style}</Style></Mod></Metadata>");
            foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
                SchemaValidator.ValidateFile(xml, Path.Combine(RepoMetadataDir, name)).Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(xml);
        }
    }

    [TestMethod]
    public void SourceSchema_RejectsInvalidInheritedColorFormat()
    {
        var xml = Path.Combine(Path.GetTempPath(), $"style-base-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, """
                <Metadata><Base ID="Shared" Kind="Mod"><Style><Controls><SecondaryButton>
                  <Active><BackgroundColor Format="ARGB">#123456</BackgroundColor></Active>
                </SecondaryButton></Controls></Style></Base></Metadata>
                """);
            var errors = SchemaValidator.ValidateFile(xml, Path.Combine(RepoMetadataDir, SchemaValidator.SourceSchemaFileName));
            errors.Should().Contain(error => error.Contains("ARGB") && error.Contains("BackgroundColor"));
        }
        finally
        {
            File.Delete(xml);
        }
    }
}
