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

        var missing = MetadataValidator.FindMissingReferences(root);
        Assert.AreEqual(2, missing.Count);
        Assert.IsTrue(missing.Any(item => item.Contains("article", StringComparison.Ordinal)));
        Assert.IsTrue(missing.Any(item => item.Contains("missing", StringComparison.Ordinal)));
    }
}
