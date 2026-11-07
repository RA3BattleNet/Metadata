using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class RegistrationValidationTests
{
    [TestMethod]
    public void DuplicateRegistrationIds_AreRejectedAcrossKindsAndCase()
    {
        var root = MetadataParser.Load(XDocument.Parse("""
            <Metadata>
              <Image ID="shared" Source="icon.png" />
              <Markdown ID="SHARED" Source="post.md" />
              <Mod ID="Demo"><Icon>shared</Icon></Mod>
            </Metadata>
            """));

        Assert.ThrowsExactly<InvalidOperationException>(() => MetadataValidator.EnsureReferences(root));
    }

    [TestMethod]
    public void LinkIcon_RequiresRegisteredImage()
    {
        var root = MetadataParser.Load(XDocument.Parse("""
            <Metadata>
              <Image ID="image" Source="icon.png" />
              <Markdown ID="article" Source="post.md" />
              <Mod ID="Demo">
                <Links>
                  <Link><Icon>image</Icon></Link>
                  <Link><Icon>article</Icon></Link>
                  <Link><Icon>missing</Icon></Link>
                  <Link />
                </Links>
              </Mod>
            </Metadata>
            """));

        CollectionAssert.AreEquivalent(new[]
        {
            "Mod (Demo): Link Icon -> article",
            "Mod (Demo): Link Icon -> missing"
        }, MetadataValidator.FindMissingReferences(root).ToArray());
    }
}
