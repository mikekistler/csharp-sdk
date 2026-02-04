using ModelContextProtocol.Server;

namespace ModelContextProtocol.Tests.Server;

public class McpHeaderAttributeTests
{
    [Fact]
    public void Constructor_ValidName_SetsName()
    {
        var attr = new McpHeaderAttribute("Region");
        Assert.Equal("Region", attr.Name);
    }

    [Theory]
    [InlineData("Region")]
    [InlineData("TenantId")]
    [InlineData("Priority")]
    [InlineData("X-Custom-Header")]
    [InlineData("header123")]
    [InlineData("a")] // Minimum valid name
    public void Constructor_ValidNames_DoesNotThrow(string name)
    {
        var attr = new McpHeaderAttribute(name);
        Assert.Equal(name, attr.Name);
    }

    [Theory]
    [InlineData("My Region")] // Space
    [InlineData("Region:Primary")] // Colon
    [InlineData(" Region")] // Leading space
    [InlineData("Region ")] // Trailing space
    public void Constructor_InvalidCharacters_ThrowsArgumentException(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new McpHeaderAttribute(name));
        Assert.Contains("invalid character", ex.Message);
    }

    [Theory]
    [InlineData("Région")] // Non-ASCII
    [InlineData("日本語")] // Non-ASCII
    [InlineData("Héllo")] // Non-ASCII
    public void Constructor_NonAsciiCharacters_ThrowsArgumentException(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new McpHeaderAttribute(name));
        Assert.Contains("invalid character", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_NullOrWhitespace_ThrowsArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(() => new McpHeaderAttribute(name!));
    }

    [Fact]
    public void Constructor_ControlCharacters_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new McpHeaderAttribute("Test\tTab"));
        Assert.Contains("invalid character", ex.Message);
    }
}
