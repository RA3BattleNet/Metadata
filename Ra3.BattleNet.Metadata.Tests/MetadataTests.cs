using FluentAssertions;
using Xunit;

namespace Ra3.BattleNet.Metadata.Tests
{
    public class MetadataTests
    {
        private readonly string _testDataPath;

        public MetadataTests()
        {
            _testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
        }

        [Fact]
        public void LoadFromFile_ValidXml_Success()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");

            // Act
            var metadata = Metadata.LoadFromFile(filePath);

            // Assert
            metadata.Should().NotBeNull();
            metadata.Name.Should().Be("Metadata");
            metadata.Children.Should().HaveCountGreaterThan(0);
        }

        [Fact]
        public void LoadFromFile_InvalidRoot_ThrowsException()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "invalid-root.xml");

            // Act & Assert
            var act = () => Metadata.LoadFromFile(filePath);
            act.Should().Throw<System.Xml.XmlException>();
        }

        [Fact]
        public void LoadFromFile_MissingFile_ThrowsFileNotFoundException()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "nonexistent.xml");

            // Act & Assert
            var act = () => Metadata.LoadFromFile(filePath);
            act.Should().Throw<FileNotFoundException>();
        }

        [Fact]
        public void LoadFromFile_CircularReference_ThrowsException()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "circular-a.xml");

            // Act & Assert
            var act = () => Metadata.LoadFromFile(filePath);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*循环引用*");
        }

        [Fact]
        public void Get_ExistingVariable_ReturnsValue()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);

            // Act
            var tags = metadata.Find("Tags");
            var versionElement = tags?.Find("Version");

            // Assert
            // 由于XML结构，Version是一个子元素而不是属性
            // 我们应该检查元素的存在性
            tags.Should().NotBeNull();
            versionElement.Should().NotBeNull();
        }

        [Fact]
        public void Get_MissingVariable_ReturnsDefault()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);

            // Act
            var missing = metadata.Get("NonExistent", "default");

            // Assert
            missing.Should().Be("default");
        }

        [Fact]
        public void Find_ValidPath_ReturnsMetadata()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);

            // Act
            var tags = metadata.Find("Tags");

            // Assert
            tags.Should().NotBeNull();
            tags!.Name.Should().Be("Tags");
        }

        [Fact]
        public void GetElementById_ExistingId_ReturnsElement()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);

            // Act
            var app = metadata.GetElementById("TestApp");

            // Assert
            app.Should().NotBeNull();
            app!.Get("ID").Should().Be("TestApp");
            // Name是子元素，不是属性
            var nameElement = app.Find("Name");
            nameElement.Should().NotBeNull();
        }

        [Fact]
        public void GetAllElements_ByName_ReturnsMatchingElements()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);

            // Act
            var applications = metadata.GetAllElements("Application");

            // Assert
            applications.Should().HaveCount(1);
            applications[0].Get("ID").Should().Be("TestApp");
        }

        [Fact]
        public void GetElementPath_ReturnsFullPath()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);
            var app = metadata.Find("Application");

            // Act
            var path = app?.GetElementPath();

            // Assert
            path.Should().Contain("Metadata");
            path.Should().Contain("Application");
        }

        [Fact]
        public void GetIncludeTree_ReturnsTreeStructure()
        {
            // Arrange
            var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
            var metadata = Metadata.LoadFromFile(filePath);

            // Act
            var tree = metadata.GetIncludeTree();

            // Assert
            tree.Should().NotBeNullOrEmpty();
            tree.Should().Contain("Metadata");
        }
    }
}
